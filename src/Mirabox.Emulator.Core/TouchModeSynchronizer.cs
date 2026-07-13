namespace Mirabox.Emulator.Core;

/// <summary>
/// Arbitrates between a mode selected on the device and mode-related output
/// traffic from Stream Dock. Repeated image frames describe the current layer;
/// they are not repeated requests to undo a local vertical swipe.
/// </summary>
public sealed class TouchModeSynchronizer
{
    private bool? _applicationTouchBar;
    private bool _hasLocalOverride;
    private bool _hasProtocolMode;

    public void SelectLocally() => _hasLocalOverride = true;

    public bool? ObserveLayer(bool touchBar)
    {
        if (_hasProtocolMode) return null;

        var applicationChangedMode = _applicationTouchBar != touchBar;
        _applicationTouchBar = touchBar;
        if (_hasLocalOverride && !applicationChangedMode) return null;

        _hasLocalOverride = false;
        return touchBar;
    }

    public bool ObserveProtocolMode(bool touchBar)
    {
        _hasProtocolMode = true;
        _hasLocalOverride = false;
        _applicationTouchBar = touchBar;
        return touchBar;
    }
}
