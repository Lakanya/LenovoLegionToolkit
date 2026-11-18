using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LenovoLegionToolkit.Lib.Listeners;

public class VolumeListener : IListener<VolumeListener.ChangedEventArgs>, IMMNotificationClient, IDisposable
{
    public class ChangedEventArgs(bool speakerMute) : EventArgs
    {
        public bool SpeakerMute { get; } = speakerMute;
    }

    public event EventHandler<ChangedEventArgs>? Changed;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly object _muteStateLock = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _speakerDevice;
    private string? _speakerDeviceId;
    private string? _speakerDeviceName;
    private bool _disposed;

    private bool? _lastMuteState;

    public async Task StartAsync()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);

            try
            {
                _speakerDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"VolumeListener: GetDefaultAudioEndpoint failed on start: {ex.Message}, falling back to enumeration.");
                _speakerDevice = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault();
            }

            if (_speakerDevice != null)
            {
                try
                {
                    _speakerDeviceId = _speakerDevice.ID;
                    _speakerDeviceName = _speakerDevice.FriendlyName;
                    _lastMuteState = _speakerDevice.AudioEndpointVolume.Mute;
                    _speakerDevice.AudioEndpointVolume.OnVolumeNotification += OnSpeakerVolumeNotification;
                    Log.Instance.Trace($"VolumeListener started. [device={_speakerDeviceName}, mute={_lastMuteState}]");

                    await SpecialKeyLedHelper.SetLedAsync(_lastMuteState.Value ? SpecialKeyLedState.SpeakerOn : SpecialKeyLedState.SpeakerOff).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Instance.Trace($"VolumeListener: failed to init device [{_speakerDeviceName}]: {ex.Message}");
                    _speakerDevice.Dispose();
                    _speakerDevice = null;
                    _speakerDeviceId = null;
                    _speakerDeviceName = null;
                    _lastMuteState = null;
                }
            }
            else
            {
                Log.Instance.Trace($"VolumeListener started, no active render device found.");
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"VolumeListener start failed: {ex.Message}", ex);
        }
    }

    public Task StopAsync()
    {
        if (_speakerDevice != null)
        {
            try
            {
                _speakerDevice.AudioEndpointVolume.OnVolumeNotification -= OnSpeakerVolumeNotification;
                _speakerDevice.Dispose();
            }
            catch { /* Ignore cross-apartment disposal errors */ }
        }

        _speakerDevice = null;
        _speakerDeviceId = null;
        _speakerDeviceName = null;

        if (_enumerator != null)
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
            _enumerator = null;
        }

        try
        {
            _initLock.Dispose();
        }
        catch (ObjectDisposedException) { }

        return Task.CompletedTask;
    }

    public async Task NotifyAsync(bool speakerMute)
    {
        await OnChangedAsync(speakerMute).ConfigureAwait(false);
    }

    protected virtual Task OnChangedAsync(bool speakerMute)
    {
        RaiseChanged(speakerMute);
        return Task.CompletedTask;
    }

    protected void RaiseChanged(bool speakerMute)
    {
        Changed?.Invoke(this, new ChangedEventArgs(speakerMute));
    }

    private async void OnSpeakerVolumeNotification(AudioVolumeNotificationData data)
    {
        try
        {
            Log.Instance.Trace($"VolumeListener notification: muted={data.Muted}, lastMute={_lastMuteState}");

            var sourceDevice = _speakerDevice;
            if (sourceDevice == null)
                return;

            lock (_muteStateLock)
            {
                if (_lastMuteState == data.Muted)
                    return;

                _lastMuteState = data.Muted;
            }

            if (sourceDevice != _speakerDevice)
                return;

            await SpecialKeyLedHelper.SetLedAsync(data.Muted ? SpecialKeyLedState.SpeakerOn : SpecialKeyLedState.SpeakerOff).ConfigureAwait(false);

            await OnChangedAsync(data.Muted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"VolumeListener error: {ex.Message}", ex);
        }
    }

    public void OnDefaultDeviceChanged(DataFlow dataFlow, Role deviceRole, string defaultDeviceId)
    {
        Log.Instance.Trace($"VolumeListener OnDefaultDeviceChanged: flow={dataFlow}, role={deviceRole}, id={defaultDeviceId}");

        if (dataFlow != DataFlow.Render) return;

        ScheduleSync(() => SyncToDevice(defaultDeviceId));
    }

    public void OnDeviceAdded(string deviceId)
    {
        Log.Instance.Trace($"VolumeListener OnDeviceAdded: id={deviceId}");
        HandleDeviceNotification(deviceId);
    }

    public void OnDeviceRemoved(string deviceId)
    {
        Log.Instance.Trace($"VolumeListener OnDeviceRemoved: id={deviceId}");

        if (_speakerDeviceId == deviceId)
        {
            Log.Instance.Trace($"VolumeListener: tracked device removed, syncing to active device.");
            ScheduleSync(SyncToActiveDevice);
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        Log.Instance.Trace($"VolumeListener OnDeviceStateChanged: id={deviceId}, state={newState}");
        HandleDeviceNotification(deviceId);
    }

    public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey)
    {
    }

    private void HandleDeviceNotification(string deviceId)
    {
        ScheduleSync(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            if (device == null)
            {
                Log.Instance.Trace($"VolumeListener: GetDevice({deviceId}) returned null on notification, syncing to be safe.");
                SyncToActiveDevice();
                return;
            }

            var isRender = device.DataFlow == DataFlow.Render;
            device.Dispose();

            if (isRender)
                SyncToActiveDevice();
        });
    }

    private void ScheduleSync(Action syncAction)
    {
        Task.Run(async () =>
        {
            try
            {
                await _initLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_enumerator == null || _disposed)
                    return;

                syncAction();
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"VolumeListener sync error: {ex.Message}", ex);
            }
            finally
            {
                try { _initLock.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    private void SyncToActiveDevice()
    {
        if (_enumerator == null) return;

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"VolumeListener: GetDefaultAudioEndpoint failed: {ex.Message}, falling back to enumeration.");
            device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault();
        }

        if (device == null)
        {
            ClearTrackedDevice();
            return;
        }

        SubscribeToDevice(device);
    }

    private void SyncToDevice(string deviceId)
    {
        if (_enumerator == null) return;

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);

        if (device == null)
        {
            Log.Instance.Trace($"VolumeListener: GetDevice({deviceId}) returned null, falling back to enumeration.");
            SyncToActiveDevice();
            return;
        }

        SubscribeToDevice(device);
    }

    private void SubscribeToDevice(MMDevice device)
    {
        var newDeviceId = device.ID;
        var newDeviceName = device.FriendlyName;

        if (_speakerDeviceId == newDeviceId)
        {
            Log.Instance.Trace($"VolumeListener: already tracking device [{newDeviceName}], skipping.");
            device.Dispose();
            return;
        }

        var previousMute = _lastMuteState;

        bool newMute;
        try
        {
            newMute = device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"VolumeListener: failed to read mute state from [{newDeviceName}]: {ex.Message}");
            device.Dispose();
            return;
        }

        if (_speakerDevice != null)
        {
            var oldName = _speakerDeviceName;
            try
            {
                _speakerDevice.AudioEndpointVolume.OnVolumeNotification -= OnSpeakerVolumeNotification;
                _speakerDevice.Dispose();
            }
            catch { /* Ignore cross-apartment disposal errors */ }
            Log.Instance.Trace($"VolumeListener unsubscribed from: {oldName}");
        }

        _speakerDevice = device;
        _speakerDeviceId = newDeviceId;
        _speakerDeviceName = newDeviceName;
        _lastMuteState = newMute;
        device.AudioEndpointVolume.OnVolumeNotification += OnSpeakerVolumeNotification;
        Log.Instance.Trace($"VolumeListener subscribed. [device={newDeviceName}, mute={_lastMuteState}, previousMute={previousMute}]");

        _ = SpecialKeyLedHelper.SetLedAsync(newMute ? SpecialKeyLedState.SpeakerOn : SpecialKeyLedState.SpeakerOff);

        if (previousMute != newMute && previousMute.HasValue)
        {
            Log.Instance.Trace($"VolumeListener mute state changed after sync. [old={previousMute}, new={newMute}]");
            _ = OnChangedAsync(newMute);
        }
    }

    private void ClearTrackedDevice()
    {
        if (_speakerDevice == null) return;

        try
        {
            _speakerDevice.AudioEndpointVolume.OnVolumeNotification -= OnSpeakerVolumeNotification;
            _speakerDevice.Dispose();
        }
        catch { /* Ignore cross-apartment disposal errors */ }

        _speakerDevice = null;
        _speakerDeviceId = null;
        _speakerDeviceName = null;
        _lastMuteState = null;
        Log.Instance.Trace($"VolumeListener: no render devices, cleared tracked device.");
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopAsync().GetAwaiter().GetResult();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
