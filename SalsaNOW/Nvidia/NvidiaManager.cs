using System;
using System.Runtime.InteropServices;

namespace SalsaNOW
{   // Nvidia NVAPI Implementation
    // Credit: https://github.com/mercuryy-1337/
    internal static class NvidiaManager
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvAPI_QueryInterface(uint id);

        // Core NVAPI function IDs
        private const uint ID_NvAPI_Initialize = 0x0150E828;
        private const uint ID_NvAPI_Unload = 0xD22BDD7E;
        private const uint ID_NvAPI_DRS_CreateSession = 0x0694D52E;
        private const uint ID_NvAPI_DRS_DestroySession = 0xDAD9CFF8;
        private const uint ID_NvAPI_DRS_LoadSettings = 0x375DBD6B;
        private const uint ID_NvAPI_DRS_SaveSettings = 0xFCBC7E14;
        private const uint ID_NvAPI_DRS_RestoreAllDefaults = 0x5927B094;
        private const uint ID_NvAPI_DRS_GetBaseProfile = 0xDA8466A0;
        private const uint ID_NvAPI_DRS_SetSetting = 0x577DD202;

        // Status code sanity check
        private const int NVAPI_OK = 0;

        // Power Management - Mode  (https://github.com/NVIDIA/nvapi/blob/9296d671e71608d6d6b7749ed93989af4ada8858/NvApiDriverSettings.h#L222)
        private const uint SETTING_ID_POWER_MANAGEMENT_MODE = 0x1057EB71;
        private const uint POWER_MANAGEMENT_PREFER_MAX = 0x00000001;

        // NVDRS_SETTING_V1 is 12320 bytes, and these constants are the byte offsets for version/settingId/type/current DWORD value.
        // Ref: NVIDIA NVAPI header "nvapi.h", struct _NVDRS_SETTING_V1.
        private const int NVDRS_SETTING_SIZE = 12320;
        private const uint NVDRS_SETTING_VER = 0x00013020;
        private const int OFFSET_SETTING_ID = 4100;
        private const int OFFSET_SETTING_TYPE = 4104;
        private const int OFFSET_CURRENT_VALUE = 8220;
        private const uint NVDRS_DWORD_TYPE = 0;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_Initialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_Unload();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_CreateSession(out IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_DestroySession(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_LoadSettings(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_SaveSettings(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_RestoreAllDefaults(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_GetBaseProfile(IntPtr hSession, out IntPtr hProfile);
        // pSetting is IntPtr because we marshal the NVDRS_SETTING struct manually
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Del_NvAPI_DRS_SetSetting(IntPtr hSession, IntPtr hProfile, IntPtr pSetting);

        private static T GetDelegate<T>(uint id) where T : Delegate
        {
            IntPtr ptr = NvAPI_QueryInterface(id);
            if (ptr == IntPtr.Zero)
                throw new EntryPointNotFoundException($"NVAPI function 0x{id:X8} could not be resolved. Ensure an NVIDIA GPU driver is installed.");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
        
        // we build a clean NVDRS_SETTING block here because SetSetting expects the native layout.
        // basically adapting NVDRS_SETTING_V1 from NVAPI headers so this matches what NPI does.
        private static IntPtr AllocDwordSetting(uint settingId, uint dwordValue)
        {
            IntPtr p = Marshal.AllocHGlobal(NVDRS_SETTING_SIZE);
            // zero the entire struct (unused fields must be 0)
            Marshal.Copy(new byte[NVDRS_SETTING_SIZE], 0, p, NVDRS_SETTING_SIZE);
            Marshal.WriteInt32(p, 0, unchecked((int)NVDRS_SETTING_VER));
            Marshal.WriteInt32(p, OFFSET_SETTING_ID, unchecked((int)settingId));
            Marshal.WriteInt32(p, OFFSET_SETTING_TYPE, unchecked((int)NVDRS_DWORD_TYPE));
            Marshal.WriteInt32(p, OFFSET_CURRENT_VALUE, unchecked((int)dwordValue));
            return p;
        }

        public static void EnableRTX()
        {
            var initialize = GetDelegate<Del_NvAPI_Initialize>(ID_NvAPI_Initialize);
            int status = initialize();
            if (status != NVAPI_OK)
                throw new InvalidOperationException($"NvAPI_Initialize failed with status {status}.");

            IntPtr hSession = IntPtr.Zero;
            try
            {
                var createSession = GetDelegate<Del_NvAPI_DRS_CreateSession>(ID_NvAPI_DRS_CreateSession);
                status = createSession(out hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_CreateSession failed with status {status}.");

                var loadSettings = GetDelegate<Del_NvAPI_DRS_LoadSettings>(ID_NvAPI_DRS_LoadSettings);
                status = loadSettings(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_LoadSettings failed with status {status}.");

                var restoreDefaults = GetDelegate<Del_NvAPI_DRS_RestoreAllDefaults>(ID_NvAPI_DRS_RestoreAllDefaults);
                status = restoreDefaults(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_RestoreAllDefaults failed with status {status}.");

                // setting the Power Management Mode to Prefer maximum performance, 
                // very important that this is applied on the base global profile so it covers every app/game launched
                var getBaseProfile = GetDelegate<Del_NvAPI_DRS_GetBaseProfile>(ID_NvAPI_DRS_GetBaseProfile);
                IntPtr hProfile;
                status = getBaseProfile(hSession, out hProfile);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_GetBaseProfile failed with status {status}.");

                var setSetting = GetDelegate<Del_NvAPI_DRS_SetSetting>(ID_NvAPI_DRS_SetSetting);
                IntPtr pSetting = IntPtr.Zero;
                try
                {
                    pSetting = AllocDwordSetting(SETTING_ID_POWER_MANAGEMENT_MODE, POWER_MANAGEMENT_PREFER_MAX);
                    status = setSetting(hSession, hProfile, pSetting);
                    if (status != NVAPI_OK)
                        throw new InvalidOperationException($"NvAPI_DRS_SetSetting (PowerMgmt) failed with status {status}.");
                }
                finally
                {
                    if (pSetting != IntPtr.Zero)
                        Marshal.FreeHGlobal(pSetting);
                }

                var saveSettings = GetDelegate<Del_NvAPI_DRS_SaveSettings>(ID_NvAPI_DRS_SaveSettings);
                status = saveSettings(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_SaveSettings failed with status {status}.");
            }
            finally
            {
                if (hSession != IntPtr.Zero)
                {
                    try
                    {
                        var destroySession = GetDelegate<Del_NvAPI_DRS_DestroySession>(ID_NvAPI_DRS_DestroySession);
                        destroySession(hSession);
                    }
                    catch { /* best-effort cleanup */ }
                }

                try
                {
                    var unload = GetDelegate<Del_NvAPI_Unload>(ID_NvAPI_Unload);
                    unload();
                }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}