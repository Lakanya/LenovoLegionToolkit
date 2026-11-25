using System;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace LenovoLegionToolkit.Lib.Services;

public class CpuEppBoostService(ApplicationSettings settings)
{
    private static readonly Guid SubProcessorGuid = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    // Energy Performance Preference (P-cores / primary class)
    private static readonly Guid PerfEppGuid = Guid.Parse("36687f9e-e3a5-4dbf-b1dc-15eb381c50ef");
    // Energy Performance Preference Class 1 (heterogeneous E-cores / Zen 5c)
    private static readonly Guid PerfEpp1Guid = Guid.Parse("36687f9e-e3a5-4dbf-b1dc-15eb381c50f0");

    public static uint GetTargetAcEpp(PowerModeState state) => state switch
    {
        PowerModeState.Performance or PowerModeState.Extreme or PowerModeState.GodMode => 0u,
        PowerModeState.Balance => 20u,
        PowerModeState.Quiet => 60u,
        _ => 20u
    };

    public unsafe void ApplyForPowerMode(PowerModeState state)
    {
        if (!settings.Store.AutoCpuEppBoostEnabled)
            return;

        var targetEpp = GetTargetAcEpp(state);

        try
        {
            if (PInvoke.PowerGetActiveScheme(null, out var activeSchemePtr) != WIN32_ERROR.ERROR_SUCCESS || activeSchemePtr is null)
            {
                Log.Instance.Trace($"CpuEppBoostService: PowerGetActiveScheme failed.");
                return;
            }

            var schemeGuid = *activeSchemePtr;

            var r0 = PInvoke.PowerWriteACValueIndex(NullSafeHandle.Null, schemeGuid, SubProcessorGuid, PerfEppGuid, targetEpp);
            var r1 = PInvoke.PowerWriteACValueIndex(NullSafeHandle.Null, schemeGuid, SubProcessorGuid, PerfEpp1Guid, targetEpp);
            var ra = PInvoke.PowerSetActiveScheme(null, schemeGuid);

            Log.Instance.Trace($"CpuEppBoostService: applied AC PERFEPP/PERFEPP1={targetEpp}% for {state} [r0={r0}, r1={r1}, activate={ra}].");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"CpuEppBoostService: failed to apply AC EPP for {state}.", ex);
        }
    }
}
