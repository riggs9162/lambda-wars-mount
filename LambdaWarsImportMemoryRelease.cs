using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime;
using Sandbox.Navigation;

namespace Sandbox.Mounting.Source1;

/// <summary>Frees what loading a battlefield left behind, once the game reports that the map is ready.</summary>
internal sealed class LambdaWarsImportMemoryRelease : ResourceLoader<LambdaWarsMount>
{
	protected override object Load()
	{
		Host.ReleasePreparedMaps();
		DropIdleNavigationGenerators();
		GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true );
		return "";
	}

	/// <summary>The engine keeps one idle navigation generator per CPU thread, each holding its largest working arrays. It creates new ones if the navigation mesh is rebuilt, so dropping the idle ones is safe. Missing engine fields are skipped.</summary>
	private static void DropIdleNavigationGenerators()
	{
		const BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
		var navigation = typeof( NavMesh );
		Clear( navigation.GetField( "HeightFieldGeneratorPool", Hidden )?.GetValue( null ) );
		Clear( navigation.GetField( "NavMeshGeneratorPool", Hidden )?.GetValue( null ) );
		ClearQueue( navigation.Assembly.GetType( "Sandbox.Navigation.Generation.CompactHeightfield" )?.GetField( "_pool", Hidden )?.GetValue( null ) );

		void Clear( object pool ) => ClearQueue( pool?.GetType().GetField( "_pool", Hidden )?.GetValue( pool ) );
	}

	private static void ClearQueue( object queue ) => queue?.GetType().GetMethod( nameof( ConcurrentQueue<object>.Clear ) )?.Invoke( queue, null );
}
