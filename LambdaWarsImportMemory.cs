using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

/// <summary>Reads current physical memory availability for trusted native import limits.</summary>
internal static class LambdaWarsImportMemory
{
	internal static (ulong Total, ulong Available) Read()
	{
		if ( !OperatingSystem.IsWindows() )
			throw new PlatformNotSupportedException( "Original map import memory safeguards currently require Windows." );

		var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
		if ( !GlobalMemoryStatusEx( ref status ) )
			throw new Win32Exception( Marshal.GetLastWin32Error(), "Could not check available memory before importing original content." );

		return (status.TotalPhysical, status.AvailablePhysical);
	}

	internal static ulong Reserve( ulong total ) => Math.Max( 2UL * 1024 * 1024 * 1024, total / 10 );

	[DllImport( "kernel32.dll", SetLastError = true )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static extern bool GlobalMemoryStatusEx( ref MemoryStatus status );

	[StructLayout( LayoutKind.Sequential )]
	private struct MemoryStatus
	{
		internal uint Length;
		internal uint MemoryLoad;
		internal ulong TotalPhysical;
		internal ulong AvailablePhysical;
		internal ulong TotalPageFile;
		internal ulong AvailablePageFile;
		internal ulong TotalVirtual;
		internal ulong AvailableVirtual;
		internal ulong AvailableExtendedVirtual;
	}
}
