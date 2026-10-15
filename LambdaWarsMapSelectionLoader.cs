using System;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsMapSelectionRequest
{
	public string MapId { get; set; } = "";
	public string RequestId { get; set; } = "";
}

/// <summary>Stages an original map selection before the caller refreshes the mount outside the loader's own lifetime.</summary>
internal sealed class LambdaWarsMapSelectionLoader : ResourceLoader<LambdaWarsMount>
{
	protected override async Task<object> LoadAsync()
	{
		LambdaWarsMapSelectionRequest request = null;
		try
		{
			request = FileSystem.Data.ReadJsonOrDefault<LambdaWarsMapSelectionRequest>( "content_map_request.json", null );
			if ( request is null || !Guid.TryParse( request.RequestId, out _ ) )
				throw new InvalidDataException( "No valid original map selection request was provided." );
			await Host.StageMapAsync( request.MapId );
			return Json.Serialize( new
			{
				request.RequestId, Success = true, request.MapId, Host.ActiveMapId, Host.ContentRevision,
				ReloadRequired = !string.Equals( Host.ActiveMapId, request.MapId, StringComparison.OrdinalIgnoreCase ), Error = ""
			} );
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException )
		{
			return Json.Serialize( new { RequestId = request?.RequestId ?? "", Success = false, Error = exception.Message } );
		}
	}
}
