using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DocManager.Desktop;

public sealed class MainState : INotifyPropertyChanged
{
    private bool _isBusy;
    private bool _isIesPreview;
    private bool _hasPhotometryDocument;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            NotifyEditorCommands();
        }
    }

    public bool IsIesPreview
    {
        get => _isIesPreview;
        set
        {
            if (_isIesPreview == value) return;
            _isIesPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLdtEdit));
            NotifyEditorCommands();
        }
    }

    public bool HasPhotometryDocument
    {
        get => _hasPhotometryDocument;
        set
        {
            if (_hasPhotometryDocument == value) return;
            _hasPhotometryDocument = value;
            OnPropertyChanged();
            NotifyEditorCommands();
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsLdtEdit => !IsIesPreview;
    public bool CanConvertIes => IsIesPreview && HasPhotometryDocument && IsNotBusy;
    public bool CanEditLdt => IsLdtEdit && HasPhotometryDocument && IsNotBusy;
    public bool CanReloadPhotometry => HasPhotometryDocument && IsNotBusy;
    public bool CanOpenPhotometryFolder => HasPhotometryDocument && IsNotBusy;

    private void NotifyEditorCommands()
    {
        OnPropertyChanged(nameof(CanConvertIes));
        OnPropertyChanged(nameof(CanEditLdt));
        OnPropertyChanged(nameof(CanReloadPhotometry));
        OnPropertyChanged(nameof(CanOpenPhotometryFolder));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
