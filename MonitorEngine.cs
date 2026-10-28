using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoCommand.Models;
using AutoCommand.Services;
using Timer = System.Threading.Timer;

namespace AutoCommand.Matrix
{
    /// <summary>One column of the rain — the per-PID aggregation of every
    /// tracked (process → remote IP) conversation row sharing that PID.</summary>
    public class PidSnapshot
    {
        public int Pid;
        public string Name;
        public long TxPackets;
        public long TxBytes;
        public long RxPackets;
        public long RxBytes;
        public bool Blocked;        // firewall says this PID's traffic is stopped
        public DateTime LastSeenUtc;
        public bool Exited;         // process is gone (row kept for attribution)
        public int Connections;     // tracked remote-IP conversations
        public int Attempts;        // new connections opened since the previous
                                    // snapshot — blocked PIDs roll red on these
    }

    /// <summary>
    /// The engine face over AutoCommand II's cores — the exact wiring the
    /// main app's Process Monitor tab does (Views/SvchostMonitorView), with
    /// the DataGrid replaced by nothing at all: consumers poll
    /// <see cref="Latest"/> for per-PID counters and turn deltas into rain.
    ///
    ///   SysmonWatcherService  → new/changed (PID, remote-IP) rows   (push)
    ///   RawSocketSnifferService → per-packet Tx/Rx counter bumps    (shared state)
    ///   FirewallService       → blocked IP/app inventory            (poll)
    /// </summary>
    public sealed class MonitorEngine : IDisposable
    {
        /// <summary>Non-fatal diagnostics (Sysmon missing, sniffer denied…) —
        /// surfaced as a tray balloon, never fatal to the rain.</summary>
        public event Action<string> OnStatus;

        // The one shared row table both engines write into — key = remote IP.
        private readonly ConcurrentDictionary<string, SvchostMonitorItem> _trackedIps = new();

        private readonly SysmonWatcherService _sysmon;
        private readonly RawSocketSnifferService _sniffer;
        private readonly DnsResolutionService _dns;

        private readonly ConcurrentDictionary<int, int> _attemptsByPid = new();

        // Firewall truth, refreshed by the block poll (same sets the monitor
        // tab keeps: enabled vs paused, IP vs app-path).
        private readonly HashSet<string> _activeIps = new();
        private readonly HashSet<string> _pausedIps = new();
        private readonly HashSet<string> _activeApps = new();
        private readonly HashSet<string> _pausedApps = new();

        private Timer _snapshotTimer;
        private Timer _livenessTimer;
        private CancellationTokenSource _blockPollCts;
        private volatile int _blockPollSeconds;

        /// <summary>Hot-adjusts the firewall poll cadence (config reload).</summary>
        public void UpdateBlockPoll(int seconds) => _blockPollSeconds = Math.Clamp(seconds, 10, 3600);

        /// <summary>Latest per-PID aggregation; swapped atomically every
        /// ~100 ms. Deltas between successive snapshots are the rain.</summary>
        public IReadOnlyList<PidSnapshot> Latest => _latest;
        private volatile IReadOnlyList<PidSnapshot> _latest = Array.Empty<PidSnapshot>();

        public bool BlockInventoryKnown { get; private set; }

        public MonitorEngine(int blockPollSeconds)
        {
            _blockPollSeconds = blockPollSeconds;

            _dns = DnsResolutionService.Instance;
            _sysmon = new SysmonWatcherService(_trackedIps, _dns)
            {
                MonitorAllProcesses = true,
                FilterKnownCloudIps = false
            };
            _sniffer = new RawSocketSnifferService(_trackedIps);

            _sysmon.OnError += msg => OnStatus?.Invoke(msg);
            _sniffer.OnError += msg => OnStatus?.Invoke(msg);

            // Every new tracked conversation is a rain event: it registers the
            // column, and for an already-blocked PID it is a roll of red —
            // the firewall drops the packet before the wire, so the sniffer
            // alone could never show a blocked process still trying.
            _sysmon.OnNewConnectionTracked += item =>
            {
                _attemptsByPid.AddOrUpdate(item.ProcessId, 1, (_, n) => n + 1);
            };
        }

        public void Start()
        {
            // DnsResolutionService is deliberately NOT started: the rain
            // never shows hostnames, and a started resolver is the one part
            // of the core that makes its own outbound queries (reverse DNS /
            // RDAP / geo). Unstarted, it only answers from its local cache —
            // the overlay stays a pure observer with zero network footprint.

            _sysmon.Start();

            // Without Sysmon the event path is silent — the kernel TCP-table
            // snapshot still seeds every currently-established conversation,
            // so the rain starts populated instead of empty.
            try
            {
                int seeded = _sysmon.SeedBaseline();
                if (seeded > 0) OnStatus?.Invoke($"Seeded {seeded} live connection(s) from the TCP table.");
            }
            catch (Exception ex) { OnStatus?.Invoke($"Baseline seed: {ex.Message}"); }

            _sniffer.Start();

            _snapshotTimer = new Timer(_ => BuildSnapshot(), null, 100, 100);
            _livenessTimer = new Timer(_ => MarkExitedProcesses(), null, 10_000, 10_000);

            _blockPollCts = new CancellationTokenSource();
            _ = BlockPollLoopAsync(_blockPollCts.Token);
        }

        private async Task BlockPollLoopAsync(CancellationToken ct)
        {
            // First pass immediately so blocked PIDs go red right at startup.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RefreshBlockStateAsync();
                    BlockInventoryKnown = true;
                }
                catch (Exception ex)
                {
                    // Never fatal — the rain just stays green until the
                    // firewall answers (e.g. policy engine briefly busy).
                    OnStatus?.Invoke($"Block inventory: {ex.Message}");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(_blockPollSeconds), ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>Re-reads the firewall's blocked inventory and stamps every
        /// tracked row — the same Active/Paused mapping the Process Monitor
        /// tab applies (RefreshBlockStateFromFirewallAsync there).</summary>
        public async Task RefreshBlockStateAsync()
        {
            var inventory = await FirewallService.Instance.GetBlockedInventoryAsync();

            lock (_activeIps)
            {
                _activeIps.Clear(); _pausedIps.Clear();
                _activeApps.Clear(); _pausedApps.Clear();
                foreach (var t in inventory)
                {
                    if (t.Kind == "ip")
                    {
                        if (string.IsNullOrEmpty(t.Ip)) continue;
                        if (t.EnabledRules > 0) _activeIps.Add(t.Ip); else _pausedIps.Add(t.Ip);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(t.AppPath)) continue;
                        if (t.EnabledRules > 0) _activeApps.Add(t.AppPath); else _pausedApps.Add(t.AppPath);
                    }
                }
            }

            foreach (var item in _trackedIps.Values)
                StampBlockState(item);
        }

        private void StampBlockState(SvchostMonitorItem item)
        {
            string ip = item.RemoteIp ?? "";
            BlockRuleState ipState;
            BlockRuleState appState = BlockRuleState.None;
            lock (_activeIps)
            {
                ipState = _activeIps.Contains(ip) ? BlockRuleState.Active
                        : _pausedIps.Contains(ip) ? BlockRuleState.Paused
                        : BlockRuleState.None;
                // Sysmon's image path is the stable identity; the live module
                // lookup covers rows created before Sysmon stamped one.
                string appPath = !string.IsNullOrEmpty(item.ImagePath)
                    ? item.ImagePath : LiveProcessPath(item.ProcessId);
                if (!string.IsNullOrEmpty(appPath))
                {
                    appState = _activeApps.Contains(appPath) ? BlockRuleState.Active
                             : _pausedApps.Contains(appPath) ? BlockRuleState.Paused
                             : BlockRuleState.None;
                }
            }
            item.SetBlockState(ipState, null, appState, null);
        }

        private static string LiveProcessPath(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.MainModule?.FileName;
            }
            catch { return null; }
        }

        /// <summary>Aggregates the shared row table into per-PID snapshots —
        /// the renderer only ever sees this immutable list.</summary>
        private void BuildSnapshot()
        {
            try
            {
                var byPid = new Dictionary<int, PidSnapshot>();
                foreach (var item in _trackedIps.Values)
                {
                    int pid = item.ProcessId;
                    if (!byPid.TryGetValue(pid, out var s))
                    {
                        s = new PidSnapshot
                        {
                            Pid = pid,
                            Name = DisplayName(item)
                        };
                        byPid[pid] = s;
                    }
                    s.TxPackets += item.TxPackets;
                    s.TxBytes += item.TxBytes;
                    s.RxPackets += item.RxPackets;
                    s.RxBytes += item.RxBytes;
                    s.Connections++;
                    if (item.LastSeen > s.LastSeenUtc) s.LastSeenUtc = item.LastSeen;
                    if (item.IsTrafficBlocked) s.Blocked = true;
                    if (item.HasExited) s.Exited = true;
                }

                foreach (var kv in byPid)
                {
                    int attempts = _attemptsByPid.TryRemove(kv.Key, out var n) ? n : 0;
                    kv.Value.Attempts = attempts;
                }
                // Attempt counters for PIDs that vanished between snapshots.
                foreach (var pid in _attemptsByPid.Keys.ToArray())
                    _attemptsByPid.TryRemove(pid, out _);

                _latest = byPid.Values.OrderByDescending(p => p.TxBytes).ToList();
            }
            catch
            {
                // A row mid-mutation must never take the snapshot loop down.
            }
        }

        private static string DisplayName(SvchostMonitorItem item)
        {
            if (!string.IsNullOrEmpty(item.ProcessName)) return item.ProcessName;
            if (!string.IsNullOrEmpty(item.ImagePath)) return Path.GetFileName(item.ImagePath);
            return "pid " + item.ProcessId;
        }

        /// <summary>Labels rows whose PID no longer exists — a dead process
        /// must stop raining like a live one (MarkExitedProcesses in the
        /// monitor tab; rows outlive their processes on purpose).</summary>
        private void MarkExitedProcesses()
        {
            try
            {
                var live = new HashSet<int>();
                foreach (var p in Process.GetProcesses()) live.Add(p.Id);

                foreach (var item in _trackedIps.Values)
                {
                    if (!live.Contains(item.ProcessId) &&
                        DateTime.UtcNow - item.LastSeen > TimeSpan.FromSeconds(30))
                    {
                        item.MarkExited();
                    }
                }
            }
            catch { /* process enumeration refused — skip this pass */ }
        }

        public void Dispose()
        {
            _snapshotTimer?.Dispose();
            _livenessTimer?.Dispose();
            _blockPollCts?.Cancel();
            try { _sniffer.Stop(); } catch { }
            try { _sysmon.Stop(); } catch { }
        }
    }
}
