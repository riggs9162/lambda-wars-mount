using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Sandbox.Mounting.Source1;

/// <summary>Allows recently released native resources to expire before reserving the selected battlefield's validated import payload.</summary>
internal sealed class LambdaWarsMapPreparationLoader : ResourceLoader<LambdaWarsMount>
{
	protected override async Task<object> LoadAsync()
	{
		try
		{
			var elapsed = Stopwatch.StartNew();
			await CollectReleasedResources();
			while ( !HasPreflightHeadroom() && elapsed.Elapsed.TotalSeconds < 11 )
			{
				await GameTask.DelayRealtime( 500 );
				await CollectReleasedResources();
			}

			using var budget = new LambdaWarsImportBudget();
			var prepared = Host.PrepareMapContent( $"maps/{Host.ActiveMapId}.bsp", budget );
			while ( !budget.HasResourceHeadroom( prepared.DecodedUploadBytes ) && elapsed.Elapsed.TotalSeconds < 11 )
			{
				await GameTask.DelayRealtime( 500 );
				await CollectReleasedResources();
			}

			budget.ReserveResources( prepared.DecodedUploadBytes );
			return Json.Serialize( new { Success = true, IsAvailable = true, Error = "" } );
		}
		catch ( Exception exception )
		{
			return Json.Serialize( new { Success = false, IsAvailable = true, Error = exception.Message } );
		}
	}

	private static bool HasPreflightHeadroom()
	{
		var memory = LambdaWarsImportMemory.Read();
		return memory.Available >= LambdaWarsImportMemory.Reserve( memory.Total ) + 512UL * 1024 * 1024;
	}

	private static async Task CollectReleasedResources()
	{
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false );
		GC.WaitForPendingFinalizers();
		await GameTask.Yield();
		await GameTask.MainThread();
	}
}
