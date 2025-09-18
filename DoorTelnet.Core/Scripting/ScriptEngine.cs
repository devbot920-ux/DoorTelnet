using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Terminal;
using MoonSharp.Interpreter;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Scripting;

/// <summary>
/// Wraps MoonSharp to provide scripting helpers.
/// </summary>
public class ScriptEngine
{
    private readonly Script _script;
    private readonly RuleEngine _ruleEngine;
    private readonly ScreenBuffer _screen;
    internal readonly Queue<string> _sendQueue = new(); // scripted queued strings (with delays)
    private readonly Queue<char> _immediateChars = new(); // immediate user keystrokes (no delay)

    public event Action? OnConnect;
    public event Action? OnTick;

    public int InterKeyDelayMs { get; set; } = 30;

    public ScriptEngine(ScreenBuffer screen, RuleEngine ruleEngine)
    {
        _screen = screen;
        _ruleEngine = ruleEngine;
        _script = new Script(CoreModules.Preset_Complete);
        RegisterApi();
    }

    private void RegisterApi()
    {
        _script.Globals["send"] = (Action<string>)((text) =>
        {
            QueueLine(text);
        });

        _script.Globals["getScreenText"] = (Func<string>)(() => _screen.ToText());

        _script.Globals["onMatch"] = (Action<string, DynValue>)((pattern, func) =>
        {
            if (func.Type != DataType.Function) return;
            _ruleEngine.Add(pattern, m =>
            {
                try { _script.Call(func, m.Value); } catch { }
            });
        });

        _script.Globals["onTick"] = (Action<DynValue>)(func =>
        {
            if (func.Type != DataType.Function) return;
            OnTick += () => { try { _script.Call(func); } catch { } };
        });

        _script.Globals["onConnect"] = (Action<DynValue>)(func =>
        {
            if (func.Type != DataType.Function) return;
            OnConnect += () => { try { _script.Call(func); } catch { } };
        });
    }

    public void DoFile(string path) => _script.DoFile(path);

    public void QueueLine(string text)
    {
        lock (_sendQueue)
        {
            _sendQueue.Enqueue(text + "\r\n");
        }
    }

    public void QueueRaw(string text)
    {
        lock (_sendQueue)
        {
            _sendQueue.Enqueue(text);
        }
    }

    public void EnqueueImmediate(char c)
    {
        lock (_immediateChars)
        {
            _immediateChars.Enqueue(c);
        }
    }

    public bool TryDequeueImmediate(out char c)
    {
        lock (_immediateChars)
        {
            if (_immediateChars.Count > 0)
            {
                c = _immediateChars.Dequeue();
                return true;
            }
        }
        c = '\0';
        return false;
    }

    public bool TryDequeueKey(out char c)
    {
        lock (_sendQueue)
        {
            while (_sendQueue.Count > 0)
            {
                var current = _sendQueue.Peek();
                if (current.Length == 0)
                {
                    _sendQueue.Dequeue();
                    continue;
                }
                c = current[0];
                current = current.Substring(1);
                _sendQueue.Dequeue();
                if (current.Length > 0) _sendQueue.Enqueue(current);
                return true;
            }
        }
        c = '\0';
        return false;
    }

    public void RaiseConnect() => OnConnect?.Invoke();
    public void RaiseTick() => OnTick?.Invoke();
}
