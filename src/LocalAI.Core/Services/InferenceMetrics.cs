using LocalAI.Core.Exceptions;
using LocalAI.Core.Models;

namespace LocalAI.Core.Services;

public sealed record RecentFailure(DateTimeOffset At, string Code, string? Model);

public sealed record InferenceMetricsSnapshot(
    DateTimeOffset Since,
    long TotalRequests,
    long Succeeded,
    long Failed,
    long Cancelled,
    int ActiveGenerations,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    ChatCompletionStats? LastCompletionStats,
    string? LastCompletionModel,
    IReadOnlyList<RecentFailure> RecentFailures);

/// <summary>
/// In-process counters for chat requests since the API started. Values reset on restart
/// and are never synthesized: everything here is recorded from real requests.
/// </summary>
public sealed class InferenceMetrics(TimeProvider timeProvider)
{
    private const int MaxRecentFailures = 10;

    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly object _gate = new();
    private readonly LinkedList<RecentFailure> _recentFailures = new();
    private readonly DateTimeOffset _since = timeProvider.GetUtcNow();
    private long _total;
    private long _succeeded;
    private long _failed;
    private long _cancelled;
    private int _active;
    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _lastFailureAt;
    private ChatCompletionStats? _lastStats;
    private string? _lastModel;

    public void RecordRequest() => Interlocked.Increment(ref _total);

    public void RecordFailure(InferenceErrorCode code, string? model) => RecordFailure(ToCode(code), model);

    public void RecordFailure(string code, string? model)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            _failed++;
            _lastFailureAt = now;
            _recentFailures.AddFirst(new RecentFailure(now, code, model));
            if (_recentFailures.Count > MaxRecentFailures)
            {
                _recentFailures.RemoveLast();
            }
        }
    }

    /// <summary>Tracks one in-flight generation. Disposing without an outcome counts as cancelled.</summary>
    public GenerationScope BeginGeneration(string model)
    {
        Interlocked.Increment(ref _active);
        return new GenerationScope(this, model);
    }

    public InferenceMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new InferenceMetricsSnapshot(
                _since,
                Interlocked.Read(ref _total),
                _succeeded,
                _failed,
                _cancelled,
                Volatile.Read(ref _active),
                _lastSuccessAt,
                _lastFailureAt,
                _lastStats,
                _lastModel,
                _recentFailures.ToArray());
        }
    }

    public static string ToCode(InferenceErrorCode code) => code switch
    {
        InferenceErrorCode.ProviderUnavailable => "provider_unavailable",
        InferenceErrorCode.ProviderTimeout => "provider_timeout",
        InferenceErrorCode.ProviderInvalidResponse => "provider_invalid_response",
        InferenceErrorCode.ProviderError => "provider_error",
        InferenceErrorCode.ModelNotFound => "model_not_found",
        InferenceErrorCode.ModelNotAllowed => "model_not_allowed",
        InferenceErrorCode.ModelLoadFailed => "model_load_failed",
        _ => "internal_error",
    };

    public sealed class GenerationScope : IDisposable
    {
        private readonly InferenceMetrics _owner;
        private readonly string _model;
        private bool _completed;
        private int _disposed;

        internal GenerationScope(InferenceMetrics owner, string model)
        {
            _owner = owner;
            _model = model;
        }

        public void Succeeded(ChatCompletionStats? stats)
        {
            if (Complete())
            {
                lock (_owner._gate)
                {
                    _owner._succeeded++;
                    _owner._lastSuccessAt = _owner._timeProvider.GetUtcNow();
                    _owner._lastStats = stats;
                    _owner._lastModel = _model;
                }
            }
        }

        public void Failed(string code)
        {
            if (Complete())
            {
                _owner.RecordFailure(code, _model);
            }
        }

        public void Cancelled()
        {
            if (Complete())
            {
                lock (_owner._gate)
                {
                    _owner._cancelled++;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Cancelled();
            Interlocked.Decrement(ref _owner._active);
        }

        private bool Complete()
        {
            if (_completed)
            {
                return false;
            }

            _completed = true;
            return true;
        }
    }
}
