using System;

namespace Sandbox.Mounting.Source1;

/// <summary>Clips each convex brush face against its authored half-spaces, including redundant bevel planes, without enumerating plane triples.</summary>
internal static class LambdaWarsBrushHull
{
	internal static List<Vector3> Build( IReadOnlyList<(Vector3 Normal, float Distance)> planes, Action check = null )
	{
		if ( planes.Count > 1024 )
			throw new InvalidDataException( "A collision brush exceeds 1024 sides." );

		var extent = 65536f;
		foreach ( var plane in planes )
		{
			var length = plane.Normal.Length;
			if ( !float.IsFinite( length ) || length < 0.0001f || !float.IsFinite( plane.Distance ) )
				throw new InvalidDataException( "A collision brush contains an invalid plane." );

			extent = Math.Max( extent, MathF.Abs( plane.Distance ) / length * 4 );
		}
		if ( !float.IsFinite( extent ) || extent > 1048576 )
			throw new InvalidDataException( "A collision brush exceeds the supported coordinate range." );

		var hull = new List<Vector3>();
		for ( var face = 0; face < planes.Count; face++ )
		{
			check?.Invoke();
			var plane = planes[face];
			var normal = plane.Normal.Normal;
			var axis = MathF.Abs( normal.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
			var right = normal.Cross( axis ).Normal * extent;
			var up = normal.Cross( right ).Normal * extent;
			var origin = normal * (plane.Distance / plane.Normal.Length);
			var polygon = new List<Vector3>
			{
				origin - right - up,
				origin + right - up,
				origin + right + up,
				origin - right + up
			};

			for ( var side = 0; side < planes.Count && polygon.Count >= 3; side++ )
			{
				if ( side != face )
					polygon = Clip( polygon, planes[side] );
			}

			if ( polygon.Count < 3 )
				continue;

			foreach ( var point in polygon )
			{
				if ( !float.IsFinite( point.x ) || !float.IsFinite( point.y ) || !float.IsFinite( point.z ) )
					throw new InvalidDataException( "A collision brush produced a non-finite vertex." );

				if ( !hull.Any( vertex => vertex.DistanceSquared( point ) < 0.01f ) )
					hull.Add( point );
			}
		}
		return hull;
	}

	private static List<Vector3> Clip( List<Vector3> polygon, (Vector3 Normal, float Distance) plane )
	{
		var result = new List<Vector3>( polygon.Count + 1 );
		var previous = polygon[^1];
		var previousDistance = plane.Normal.Dot( previous ) - plane.Distance;
		foreach ( var current in polygon )
		{
			var distance = plane.Normal.Dot( current ) - plane.Distance;
			var previousInside = previousDistance <= 0;
			var inside = distance <= 0;
			if ( previousInside != inside )
				result.Add( previous + (current - previous) * (previousDistance / (previousDistance - distance)) );

			if ( inside )
				result.Add( current );

			previous = current;
			previousDistance = distance;
		}
		return result;
	}
}
