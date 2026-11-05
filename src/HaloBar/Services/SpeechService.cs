using System.Reflection;

namespace HaloBar.Services;

/// <summary>
/// Reads text aloud with the built-in Windows voice (SAPI, through late-bound COM so no extra
/// package is needed). Speaking is asynchronous; speaking again while it talks stops it.
/// </summary>
internal sealed class SpeechService
{
    private const uint Async = 1, PurgeBeforeSpeak = 2;
    private readonly object _gate = new();
    private object? _voice;
    private Type? _type;

    /// <summary>Roughly whether it's talking (set when asked to speak, cleared on stop).</summary>
    public bool Speaking { get; private set; }

    /// <summary>Speaks <paramref name="text"/>, or stops if it's already talking.</summary>
    public void Toggle(string? text)
    {
        if (Speaking) { Stop(); return; }
        Speak(text);
    }

    public void Speak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_gate)
            {
                var voice = Voice();
                if (voice == null) return;
                Speaking = true;
                // Async so it returns at once; purge first so a new request replaces the old one.
                _type!.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, Async | PurgeBeforeSpeak]);
            }
        }
        catch (Exception ex)
        {
            Speaking = false;
            Log.Write($"Speech: couldn't read aloud: {ex.Message}");
        }
    }

    public void Stop()
    {
        Speaking = false;
        try
        {
            lock (_gate)
            {
                if (_voice == null) return;
                // An empty, purging utterance drops whatever is queued or playing.
                _type!.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, [" ", Async | PurgeBeforeSpeak]);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Speech: couldn't stop: {ex.Message}");
        }
    }

    private object? Voice()
    {
        if (_voice != null) return _voice;
        _type = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (_type == null)
        {
            Log.Write("Speech: SAPI voice not available on this PC");
            return null;
        }
        return _voice = Activator.CreateInstance(_type);
    }
}
