using System;
using System.Globalization;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Reads Source entity numeric prefixes using invariant decimal syntax while rejecting non-finite coordinates.</summary>
internal static class LambdaWarsEntityNumbers
{
	internal static float Read( string value )
	{
		var text = value.AsSpan().TrimStart();
		var position = 0;
		if ( position < text.Length && text[position] is '+' or '-' )
			position++;

		if ( text[position..].StartsWith( "nan", StringComparison.OrdinalIgnoreCase )
			|| text[position..].StartsWith( "inf", StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Original entity coordinate is not finite." );

		var digits = 0;
		while ( position < text.Length && char.IsAsciiDigit( text[position] ) )
		{
			position++;
			digits++;
		}

		if ( position < text.Length && text[position] == '.' )
		{
			position++;
			while ( position < text.Length && char.IsAsciiDigit( text[position] ) )
			{
				position++;
				digits++;
			}
		}

		if ( digits == 0 )
			return 0;

		if ( position < text.Length && text[position] is 'e' or 'E' )
		{
			var exponent = position++;
			if ( position < text.Length && text[position] is '+' or '-' )
				position++;

			var exponentStart = position;
			while ( position < text.Length && char.IsAsciiDigit( text[position] ) )
				position++;

			if ( exponentStart == position )
				position = exponent;
		}

		var number = float.Parse( text[..position], NumberStyles.Float, CultureInfo.InvariantCulture );
		if ( !float.IsFinite( number ) )
			throw new InvalidDataException( "Original entity coordinate is not finite." );

		return number;
	}
}
