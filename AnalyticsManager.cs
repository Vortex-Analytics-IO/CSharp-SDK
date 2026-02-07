using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class AnalyticsManager
{
    // Data structures
    public class ValueWrapper { public string data { get; set; } }

    public class TrackingData
    {
        public string name { get; set; }
        public string value { get; set; }
        public string identity { get; set; }
        public string session_id { get; set; }
        public string platform { get; set; }
        public string app_version { get; set; }
        public string custom { get; set; }
        public string timestamp { get; set; }
    }

    public class Tracking
    {
        public string tenant_id { get; set; }
        public TrackingData tracking { get; set; }
    }

    public class BatchedTracks
    {
        public List<Tracking> tracks { get; set; } = new List<Tracking>();
    }

    // Verbose logging
    private bool _verbose = false;

    public void SetVerbose(bool verbose)
    {
        _verbose = verbose;
    }

    private void VortexLog(string format, params object[] args)
    {
        if (!_verbose) return;
        try
        {
            Console.WriteLine("[Vortex] " + format, args);
        }
        catch
        {
            Console.WriteLine("[Vortex] " + string.Format(format, args));
        }
    }

    // Singleton
    private static readonly Lazy<AnalyticsManager> _lazy = new Lazy<AnalyticsManager>(() => new AnalyticsManager());
    public static AnalyticsManager Instance => _lazy.Value;

    // Settings
    private string _tenantId;
    private string _url = "https://in.vortexanalytics.io";
    private string _platform;
    private bool _autoBatching = false;
    private int _autoFlushIntervalMs = 10000;

    // State
    private string _identity;
    private string _sessionId;
    private string _appVersion;
    private string _customData = "";

    private bool _initialized;
    private bool _serverAlive;
    private bool _isServerChecked;

    private bool _flushCompleted;

    private readonly List<Tracking> _internalQueue = new List<Tracking>();
    private readonly BatchedTracks _manualBatchedTracks = new BatchedTracks();
    
    private readonly object _lock = new object();
    private static readonly HttpClient _httpClient = new HttpClient();
    private CancellationTokenSource _cts;

    private AnalyticsManager() 
    {
        AppDomain.CurrentDomain.ProcessExit += (_, __) => Shutdown();
    }

    // Setup methods
    public void Init(string tenantId, string url, string platform, string appVersion = "1.0.0", bool autoBatching = false, int flushIntervalSec = 10)
    {
        if (_initialized) return;

        _tenantId = tenantId;
        _url = url.TrimEnd('/');
        _platform = platform;
        _appVersion = appVersion;
        _autoBatching = autoBatching;
        _autoFlushIntervalMs = flushIntervalSec * 1000;

        VortexLog("Init called: tenantId={0}, url={1}, platform={2}, appVersion={3}, autoBatching={4}, flushIntervalSec={5}", tenantId, url, platform, appVersion, autoBatching, flushIntervalSec);
        Initialize();
    }

    private void Initialize()
    {
        _initialized = true;
        InitSession();

        VortexLog("AnalyticsManager initialized");

        // Run server check in background
        Task.Run(CheckServerAvailabilityAsync);

        TrackEvent("app_started");
    }

    private void InitSession()
    {
        _identity = GetPersistentIdentity();
        _sessionId = Guid.NewGuid().ToString();
        VortexLog("Session initialized - Identity: {0}, SessionId: {1}, AppVersion: {2}", _identity, _sessionId, _appVersion);
    }

    private string GetPersistentIdentity()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "analytics.id");
        try
        {
            if (File.Exists(path))
            {
                var id = File.ReadAllText(path);
                VortexLog("Loaded persistent identity: {0}", id);
                return id;
            }
            string newId = Guid.NewGuid().ToString();
            File.WriteAllText(path, newId);
            VortexLog("Generated new persistent identity: {0}", newId);
            return newId;
        }
        catch (Exception ex)
        {
            VortexLog("Failed to get persistent identity: {0}", ex.Message);
            return Guid.NewGuid().ToString();
        }
    }

    private Tracking CreateTracking(string name, string value)
    {
        var trackingData = new TrackingData
        {
            name = name,
            value = value,
            identity = _identity,
            session_id = _sessionId,
            platform = _platform,
            app_version = _appVersion,
            timestamp = DateTime.UtcNow.ToString("o") // ISO 8601 format
        };

        if (!string.IsNullOrEmpty(_customData))
            trackingData.custom = _customData;

        return new Tracking
        {
            tenant_id = _tenantId,
            tracking = trackingData
        };
    }

    // Networking
    private async Task CheckServerAvailabilityAsync()
    {
        if (string.IsNullOrEmpty(_url)) return;

        VortexLog("Checking server availability at {0}/health", _url);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _httpClient.GetAsync($"{_url}/health", cts.Token);
            _serverAlive = response.IsSuccessStatusCode;
            VortexLog("Server check completed - Alive: {0}", _serverAlive ? "true" : "false");
        }
        catch (Exception ex)
        {
            _serverAlive = false;
            VortexLog("Server check failed: {0}", ex.Message);
        }

        _isServerChecked = true;

        if (_serverAlive)
        {
            if (_autoBatching) StartAutoFlush();
            else await FlushInternalQueueAsync();
        }
    }

    private async Task<bool> SendRequestAsync(string endpoint, object data)
    {
        try
        {
            string json = JsonSerializer.Serialize(data);
            VortexLog("Sending POST to {0}{1} with body: {2}", _url, endpoint, json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_url}{endpoint}", content);
            if (!response.IsSuccessStatusCode)
            {
                VortexLog("Request failed: {0}{1}", _url, endpoint);
                VortexLog("Response code: {0}", (int)response.StatusCode);
                VortexLog("Response body: {0}", await response.Content.ReadAsStringAsync());
            }
            else
            {
                VortexLog("Request succeeded: {0}{1}", _url, endpoint);
            }
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            VortexLog("Request exception: {0}", ex.Message);
            return false;
        }
    }

    // Flush logic
    private void StartAutoFlush()
    {
        _cts = new CancellationTokenSource();
        Task.Run(async () => 
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(_autoFlushIntervalMs, _cts.Token);
                if (_serverAlive) await FlushInternalQueueAsync();
            }
        }, _cts.Token);
    }

    private async Task FlushInternalQueueAsync()
    {
        List<Tracking> toSend;
        lock (_lock)
        {
            if (_internalQueue.Count == 0) return;
            toSend = new List<Tracking>(_internalQueue);
            _internalQueue.Clear();
        }

        var batch = new BatchedTracks { tracks = toSend };
        VortexLog("Flushing internal queue with {0} events", batch.tracks.Count);
        await SendRequestAsync("/batch", batch);
    }

    public void FlushManualBatch()
    {
        Task.Run(async () => 
        {
            BatchedTracks batchToSend;
            lock (_lock)
            {
                if (_manualBatchedTracks.tracks.Count == 0) return;
                // Deep copy or reassign to avoid race conditions
                batchToSend = new BatchedTracks { tracks = new List<Tracking>(_manualBatchedTracks.tracks) };
                _manualBatchedTracks.tracks.Clear();
            }

            VortexLog("Posting manual batch with {0} events", batchToSend.tracks.Count);
            await SendRequestAsync("/batch", batchToSend);
        });
    }

    // Custom Data
    public void SetCustomData(Dictionary<string, object> customData)
    {
        if (customData == null || customData.Count == 0)
        {
            _customData = "";
            return;
        }

        _customData = JsonSerializer.Serialize(customData);
    }

    public void ClearCustomData()
    {
        _customData = "";
    }

    // Public API
    public void TrackEvent(string eventName, Dictionary<string, object> props)
    {
        if (ShouldSkip()) return;
        ProcessTrackEvent(eventName, JsonSerializer.Serialize(props));
    }

    public void TrackEvent(string eventName, string props = "")
    {
        if (ShouldSkip()) return;
        string json = string.IsNullOrEmpty(props) ? "" : JsonSerializer.Serialize(new ValueWrapper { data = props });
        ProcessTrackEvent(eventName, json);
    }

    private bool ShouldSkip() => !_serverAlive && _isServerChecked && !_autoBatching;

    private void ProcessTrackEvent(string eventName, string value)
    {
        var t = CreateTracking(eventName, value);
        VortexLog("TrackEvent: {0} value: {1}", eventName, value);
        lock (_lock)
        {
            if (!_isServerChecked || _autoBatching)
            {
                _internalQueue.Add(t);
                VortexLog("Event queued internally. Queue size: {0}", _internalQueue.Count);
            }
            else
            {
                _ = SendRequestAsync("/track", t);
            }
        }
    }

    public void BatchedTrackEvent(string eventName, Dictionary<string, object> props)
    {
        if (!_serverAlive) return;
        var tracking = CreateTracking(eventName, JsonSerializer.Serialize(props));
        lock (_lock)
        {
            _manualBatchedTracks.tracks.Add(tracking);
            VortexLog("BatchedTrackEvent: {0} (dict) added to manual batch. Batch size: {1}", eventName, _manualBatchedTracks.tracks.Count);
        }
    }

    public void BatchedTrackEvent(string eventName, string props = "")
    {
        if (!_serverAlive) return;
        var tracking = CreateTracking(eventName, props);
        lock (_lock)
        {
            _manualBatchedTracks.tracks.Add(tracking);
            VortexLog("BatchedTrackEvent: {0} (string) added to manual batch. Batch size: {1}", eventName, _manualBatchedTracks.tracks.Count);
        }
    }

    // Shutdown / Cleanup
    public void Shutdown()
    {
        if (_flushCompleted) return;

        _cts?.Cancel();

        lock (_lock)
        {
            _manualBatchedTracks.tracks.Add(CreateTracking("app_exit", ""));

            if (_internalQueue.Count > 0)
            {
                _manualBatchedTracks.tracks.AddRange(_internalQueue);
                _internalQueue.Clear();
            }
        }

        if (_manualBatchedTracks.tracks.Count > 0)
        {
            VortexLog("Attempting final flush before exit with {0} events", _manualBatchedTracks.tracks.Count);
            var task = SendRequestAsync("/batch", _manualBatchedTracks);
            task.Wait(2000);
        }

        _flushCompleted = true;
    }
}
