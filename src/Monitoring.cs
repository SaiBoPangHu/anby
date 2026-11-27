using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AnbyPet
{
    internal sealed class LoadMonitor : IDisposable
    {
        private ulong idle;
        private ulong total;
        private IntPtr query;
        private IntPtr engineCounter;
        private IntPtr array;
        private uint capacity;
        private string primaryAdapterId;

        internal LoadMonitor()
        {
            Cpu();
            RefreshAdapters();
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0 &&
                PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engineCounter) == 0)
                PdhCollectQueryData(query);
        }

        internal double? Cpu()
        {
            ulong nowIdle, kernel, user;
            if (!GetSystemTimes(out nowIdle, out kernel, out user)) return null;
            ulong nowTotal = kernel + user;
            ulong oldTotal = total, oldIdle = idle;
            total = nowTotal;
            idle = nowIdle;
            if (oldTotal == 0 || nowTotal <= oldTotal || nowIdle < oldIdle) return null;
            return Math.Max(0, Math.Min(100, 100.0 * (1 - (double)(nowIdle - oldIdle) / (nowTotal - oldTotal))));
        }

        private static readonly Regex EngineName = new Regex(
            @"luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_(\d+)_eng_(\d+)_",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal double? Gpu()
        {
            string adapterId = primaryAdapterId;
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (engineCounter != IntPtr.Zero)
            {
                uint count = 0;
                uint required = capacity;
                uint status = PdhCollectQueryData(query);
                if (status == 0)
                {
                    status = PdhGetFormattedCounterArray(engineCounter, 0x200, ref required, out count, array);
                    if (status == 0x800007D2 && required > capacity && required <= 4 * 1024 * 1024)
                    {
                        if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
                        array = Marshal.AllocHGlobal((int)required);
                        capacity = required;
                        status = PdhGetFormattedCounterArray(engineCounter, 0x200, ref required, out count, array);
                    }
                    if (status == 0)
                    {
                        int size = Marshal.SizeOf(typeof(CounterItem));
                        for (int i = 0; i < count; i++)
                        {
                            var item = (CounterItem)Marshal.PtrToStructure(IntPtr.Add(array, i * size), typeof(CounterItem));
                            if (item.Status > 1 || double.IsNaN(item.Value) || double.IsInfinity(item.Value)) continue;
                            var match = EngineName.Match(Marshal.PtrToStringUni(item.Name) ?? "");
                            if (!match.Success) continue;
                            string id = match.Groups[1].Value.PadLeft(8, '0') + ":" + match.Groups[2].Value.PadLeft(8, '0');
                            if (adapterId == null) adapterId = id;
                            if (!string.Equals(adapterId, id, StringComparison.OrdinalIgnoreCase)) continue;
                            string engine = match.Groups[3].Value + ":" + match.Groups[4].Value;
                            double previous;
                            engines.TryGetValue(engine, out previous);
                            engines[engine] = previous + Math.Max(0, item.Value);
                        }
                    }
                }
            }

            return engines.Count > 0 ? Math.Min(100, engines.Values.Max()) : (double?)null;
        }

        internal void RefreshAdapters()
        {
            string next = null;
            var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            IntPtr factory;
            if (CreateDXGIFactory1(ref id, out factory) != 0) return;
            try
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1.
                var enumerate = (EnumAdapter)VirtualMethod(factory, 12, typeof(EnumAdapter));
                for (uint index = 0; index < 32; index++)
                {
                    IntPtr candidate;
                    if (enumerate(factory, index, out candidate) != 0) break;
                    try
                    {
                        var describe = (DescribeAdapter)VirtualMethod(candidate, 10, typeof(DescribeAdapter));
                        AdapterDescription desc;
                        if (describe(candidate, out desc) != 0 || (desc.Flags & 2) != 0) continue;
                        next = desc.Luid.Key();
                        break;
                    }
                    finally { Marshal.Release(candidate); }
                }
            }
            finally { Marshal.Release(factory); }
            primaryAdapterId = next;
        }

        private static Delegate VirtualMethod(IntPtr pointer, int index, Type type)
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), index * IntPtr.Size), type);
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem
        {
            internal IntPtr Name;
            internal uint Status;
            internal double Value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Name;
            internal uint Vendor, Device, Subsystem, Revision;
            internal UIntPtr VideoMemory, SystemMemory, SharedMemory;
            internal Luid Luid;
            internal uint Flags;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapter(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DescribeAdapter(IntPtr self, out AdapterDescription desc);

        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string source, IntPtr data, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string name, IntPtr data, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        internal uint Low;
        internal int High;

        internal string Key()
        {
            return High.ToString("x8", CultureInfo.InvariantCulture) + ":" + Low.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
