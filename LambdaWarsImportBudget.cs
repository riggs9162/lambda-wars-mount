using System;
using System.Diagnostics;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Bounds native import memory growth and work before loading the next resource.</summary>
internal sealed class LambdaWarsImportBudget : IDisposable
{
	private readonly Process process = Process.GetCurrentProcess();
	private readonly Stopwatch elapsed = Stopwatch.StartNew();
	private readonly long initialResident;
	private long growthLimit = 512L * 1024 * 1024;
	private long residentLimit;
	private TimeSpan nextMemoryCheck;
	internal string Stage { get; private set; } = "Preparing original map";

	internal LambdaWarsImportBudget()
	{
		initialResident = process.WorkingSet64;
		residentLimit = checked(initialResident + growthLimit);
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
			throw new InvalidDataException( $"Original map import stopped after exceeding its {growthLimit / (1024 * 1024)} MB memory growth limit ({process.WorkingSet64 / (1024 * 1024)} MB used by the game). The map remains unavailable." );
	}

	/// <summary>Reserves bounded staging, cache and native allocation headroom from validated texture payloads while retaining the system RAM reserve.</summary>
	internal void ReserveResources( long decodedUploadBytes )
	{
		var requested = ResourceGrowthLimit( decodedUploadBytes );

		var memory = LambdaWarsImportMemory.Read();
		var reserve = LambdaWarsImportMemory.Reserve( memory.Total );
		process.Refresh();
		var remaining = Math.Max( 0, requested - Math.Max( 0, process.WorkingSet64 - initialResident ) );
		if ( memory.Available < reserve || (ulong)remaining > memory.Available - reserve )
			throw new InvalidDataException( $"Original map needs {remaining / (1024 * 1024)} MB more import headroom while keeping {reserve / (1024 * 1024)} MB of RAM free. Close other applications before trying again." );

		growthLimit = requested;
		residentLimit = checked(initialResident + growthLimit);
		Log.Info( $"Original map import: reserving {growthLimit / (1024 * 1024)} MB of memory growth for {decodedUploadBytes / (1024 * 1024)} MB of validated texture payloads" );
		nextMemoryCheck = TimeSpan.Zero;
		Check();
	}

	internal bool HasResourceHeadroom( long decodedUploadBytes )
	{
		var requested = ResourceGrowthLimit( decodedUploadBytes );
		var memory = LambdaWarsImportMemory.Read();
		var reserve = LambdaWarsImportMemory.Reserve( memory.Total );
		process.Refresh();
		var remaining = Math.Max( 0, requested - Math.Max( 0, process.WorkingSet64 - initialResident ) );
		return memory.Available >= reserve && (ulong)remaining <= memory.Available - reserve;
	}

	private long ResourceGrowthLimit( long decodedUploadBytes )
	{
		if ( decodedUploadBytes < 0 )
			throw new ArgumentOutOfRangeException( nameof( decodedUploadBytes ) );
		const long allocationBlock = 64L * 1024 * 1024;
		var estimate = checked(decodedUploadBytes * 4 + 256L * 1024 * 1024);
		var requested = Math.Max( growthLimit, checked((estimate + allocationBlock - 1) / allocationBlock * allocationBlock) );
		if ( requested > 3L * 1024 * 1024 * 1024 )
			throw new InvalidDataException( "Original map resource estimates exceed the 3 GB import growth ceiling." );
		return requested;
	}

	/// <summary>Records bounded import progress and elapsed time at resource preparation boundaries.</summary>
	internal void SetStage( string stage )
	{
		Stage = stage;
		Log.Info( $"Original map import: {stage} ({elapsed.Elapsed.TotalSeconds:F2}s elapsed)" );
		Check();
	}

	public void Dispose() => process.Dispose();
}
