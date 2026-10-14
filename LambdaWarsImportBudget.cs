using System;
using System.Diagnostics;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Bounds native import memory growth and work before loading the next resource.</summary>
internal sealed class LambdaWarsImportBudget : IDisposable
{
	private readonly Process process = Process.GetCurrentProcess();
	private readonly Stopwatch elapsed = Stopwatch.StartNew();
	private readonly long residentLimit;

	internal LambdaWarsImportBudget()
	{
		residentLimit = Math.Min( 2L * 1024 * 1024 * 1024, process.WorkingSet64 + 512L * 1024 * 1024 );
		Check();
	}

	internal void Check()
	{
		process.Refresh();
		if ( process.WorkingSet64 > residentLimit )
			throw new InvalidDataException( "Original map import stopped at its memory limit. The map remains unavailable." );
		if ( elapsed.Elapsed > TimeSpan.FromSeconds( 20 ) )
			throw new InvalidDataException( "Original map import exceeded its current work budget. The map remains unavailable." );
	}

	public void Dispose() => process.Dispose();
}
