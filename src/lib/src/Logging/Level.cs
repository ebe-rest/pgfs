namespace Pgfs.Lib.Logging;

public static class Level
{
	public enum Enum
	{
		All = 0,
		Trace = 1,
		Debug = 2,
		Information = 3,
		Warning = 4,
		Error = 5,
		Critical = 6,
		None = 7,
	}

	public const Enum All = Enum.All;
	public const Enum Trace = Enum.Trace;
	public const Enum Debug = Enum.Debug;
	public const Enum Information = Enum.Information;
	public const Enum Warning = Enum.Warning;
	public const Enum Error = Enum.Error;
	public const Enum Critical = Enum.Critical;
	public const Enum None = Enum.None;

	public static Enum Parse(string value) => value[..1].ToLower()[0] switch {
		'a' or '0' => All,
		't' or '1' => Trace,
		'd' or '2' => Debug,
		'i' or '3' => Information,
		'w' or '4' => Warning,
		'e' or '5' => Error,
		'c' or '6' => Critical,
		'n' or '7' => None,
		_ => Information,
	};

	public static Enum Parse(int value) => value switch {
		(int)All => All,
		(int)Trace => Trace,
		(int)Debug => Debug,
		(int)Information => Information,
		(int)Warning => Warning,
		(int)Error => Error,
		(int)Critical => Critical,
		(int)None => None,
		_ => Information,
	};

	extension(Enum value)
	{
		public string String() => value switch {
			All => "all",
			Trace => "trace",
			Debug => "debug",
			Information => "information",
			Warning => "warning",
			Error => "error",
			Critical => "critical",
			None => "none",
			_ => "information",
		};

		public string ShortString() => value switch {
			All => "all",
			Trace => "trc",
			Debug => "dbg",
			Information => "inf",
			Warning => "wrn",
			Error => "err",
			Critical => "cri",
			None => "non",
			_ => "inf",
		};
	}
}
