using System.Diagnostics;
using System.Windows.Forms;

namespace TowerTapes;

public class ProcessWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _crcRunning;

    public event Action? CrcStarted;
    public event Action? CrcStopped;
    public bool IsCrcRunning => _crcRunning;

    public ProcessWatcher()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += CheckProcess;
    }

    public void Start() => _timer.Start();

    private void CheckProcess(object? sender, EventArgs e)
    {
        bool running;
        try
        {
            running = Process.GetProcessesByName("CRC").Length > 0;
        }
        catch
        {
            return;
        }

        if (running && !_crcRunning)
        {
            _crcRunning = true;
            CrcStarted?.Invoke();
        }
        else if (!running && _crcRunning)
        {
            _crcRunning = false;
            CrcStopped?.Invoke();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
