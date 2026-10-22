using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AutoVolume.App;

/// <summary>
/// 固定 WPF 混合模式程序集依赖的 VC++ 运行时。
///
/// 背景：WPF 的 DirectWriteForwarder / System.Printing 是 C++/CLI 混合模式程序集，
/// 它们在 ProcessExit 阶段执行 CRT 反初始化（__scrt_uninitialize_type_info），
/// 而 vcruntime140_cor3.dll 在那时才被**按名延迟加载**——正常运行时这个 DLL 根本没被加载过。
/// 单文件自解压模式下它只存在于 %TEMP%\.net\&lt;应用名&gt;\&lt;hash&gt;\，
/// 一旦退出时该目录不可用（临时目录清理、应用升级、安全软件搬移），
/// 按名加载失败 → 未处理异常 DllNotFoundException → 进程以 1026 事件终止。
///
/// 做法：启动时按全路径加载一次并永不释放，退出期的按名加载就会命中已加载模块。
/// 见 docs/项目现状与交接.md §6.1。
/// </summary>
internal static class NativePin
{
    private const string Name = "vcruntime140_cor3.dll";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    /// <summary>返回固定的模块句柄；0 表示没找到（不影响运行，只是失去退出期保护）。</summary>
    public static IntPtr PinVcRuntime()
    {
        // 单文件：NATIVE_DLL_SEARCH_DIRECTORIES 首个条目就是本次的解压目录
        foreach (string raw in ((AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string) ?? "")
                                .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(raw.Trim(), Name);
            if (!File.Exists(candidate)) continue;

            IntPtr handle = LoadLibraryW(candidate);
            if (handle != IntPtr.Zero) return handle;
        }

        // 框架依赖构建：运行时目录里已有同名文件，按名加载即可
        return LoadLibraryW(Name);
    }
}
