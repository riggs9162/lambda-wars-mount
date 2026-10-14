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
	private TimeSpan nextMemoryCheck;
	internal string Stage { get; private set; } = "Preparing original map";

	internal LambdaWarsImportBudget()
	{
		residentLimit = checked(process.WorkingSet64 + 512L * 1024 * 1024);
		try
		{
			Check();
		}
		catch
		{
			process.Dispose();
			throw;
		}
	}

	internal void Check()
	{
		var currentElapsed = elapsed.Elapsed;
		if ( currentElapsed > TimeSpan.FromSeconds( 20 ) )
			throw new InvalidDataException( $"Original map import exceeded its 20-second work budget during {Stage}. The map remains unavailable." );
		if ( currentElapsed < nextMemoryCheck )
			return;

		nextMemoryCheck = currentElapsed + TimeSpan.FromMilliseconds( 50 );
		var memory = LambdaWarsImportMemory.Read();
		var reserve = LambdaWarsImportMemory.Reserve( memory.Total );
		if ( memory.Available < reserve )
			throw new InvalidDataException( $"Original map import stopped: {memory.Available / (1024 * 1024)} MB of RAM is available; at least {reserve / (1024 * 1024)} MB must remain free. Close other applications before trying again." );

		process.Refresh();
		if ( process.WorkingSet64 > residentLimit )
			throw new InvalidDataException( $"Original map import stopped after exceeding its 512 MB memory growth limit ({process.WorkingSet64 / (1024 * 1024)} MB used by the game). The map remains unavailable." );
	}

	/// <summary>Records bounded import progress and elapsed time at resource preparation boundaries.</summary>
	internal void SetStage( string stage )
	{
		Stage = stage;
		Log.Info( $"Original map import: {stage} ({elapsed.Elapsed.TotalSeconds:F2}s elapsed)" );
		nextMemoryCheck = TimeSpan.Zero;
		Check();
	}

	public void Dispose() => process.Dispose();
}
