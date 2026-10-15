using System;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsContentPathRequest
{
	public string Path { get; set; } = "";
	public string RequestId { get; set; } = "";
}

internal sealed class LambdaWarsContentPathReceipt
{
	public string RequestId { get; init; } = "";
	public bool Success { get; init; }
	public string Error { get; init; } = "";
	public string SavedPath { get; init; } = "";
	public string ActiveInstallationPath { get; init; } = "";
	public string ResolvedPath { get; init; } = "";
	public string Source { get; init; } = "None";
	public bool RestartRequired { get; init; }
	public bool ReloadRequired { get; init; }
	public bool SupportsLiveReload => true;
	public bool IsAvailable { get; init; }
	public string Status { get; init; } = "";
	public string ConfigurationPath { get; init; } = "";
}

/// <summary>Provides uncached status or explicitly requested installation changes through a mounted GameResource loader.</summary>
internal sealed class LambdaWarsConfigurationLoader( string steamInstallationPath, Func<string> activeInstallationPath, bool apply = false ) : ResourceLoader<LambdaWarsMount>
{
	private string lastRequestId;
	private string lastReceipt;

	protected override async Task<object> LoadAsync()
	{
		if ( !apply )
			return Json.Serialize( Receipt( "", true, false, "" ) );

		LambdaWarsContentPathRequest request = null;
		try
		{
			request = FileSystem.Data.ReadJsonOrDefault<LambdaWarsContentPathRequest>( "content_mount_request.json", null );
			if ( request is null || !Guid.TryParse( request.RequestId, out _ ) )
				throw new InvalidDataException( "No valid installation change request was provided." );

			if ( request.RequestId == lastRequestId )
				return lastReceipt;

			await Host.StageInstallationAsync( request.Path );
			lastRequestId = request.RequestId;
			lastReceipt = Json.Serialize( Receipt( request.RequestId, true, true, "" ) );
			return lastReceipt;
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException )
		{
			return Json.Serialize( Receipt( request?.RequestId ?? "", false, false, exception.Message ) );
		}
	}

	private LambdaWarsContentPathReceipt Receipt( string requestId, bool success, bool restartRequired, string error )
	{
		var selection = LambdaWarsInstallation.Resolve( steamInstallationPath, Environment.GetEnvironmentVariable( "LAMBDA_WARS_GAME_PATH" ) );
		var active = activeInstallationPath() ?? "";
		var saved = selection.ConfiguredInstallationPath;
		var status = selection.Status;
		if ( restartRequired )
			status = string.IsNullOrWhiteSpace( saved )
				? "Manual path cleared. Applying Steam installation detection."
				: $"Installation path validated. Applying: {selection.InstallationPath}";

		return new()
		{
			RequestId = requestId,
			Success = success,
			Error = string.IsNullOrEmpty( error ) ? selection.Error : error,
			SavedPath = saved,
			ActiveInstallationPath = active,
			ResolvedPath = selection.InstallationPath,
			Source = selection.Source,
			RestartRequired = false,
			ReloadRequired = restartRequired || !string.Equals( active, selection.InstallationPath, StringComparison.OrdinalIgnoreCase ),
			IsAvailable = selection.IsAvailable,
			Status = success ? status : "Installation path was not changed.",
			ConfigurationPath = selection.ConfigurationPath
		};
	}
}

