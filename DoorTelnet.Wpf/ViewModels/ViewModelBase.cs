using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.ViewModels;

public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    protected readonly ILogger _logger;
    private bool _isDisposed;

    protected ViewModelBase(ILogger logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        try { OnDisposing(); } catch { }
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDisposing() { }
}
