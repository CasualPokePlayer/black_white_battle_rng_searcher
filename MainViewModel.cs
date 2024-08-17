using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Program;

public partial class MainViewModel : ObservableObject
{
	[ObservableProperty]
	private string? _macAddress;

	[ObservableProperty]
	private int _versionSelection;

	[ObservableProperty]
	private decimal? _vFrameMin;

	[ObservableProperty]
	private decimal? _vFrameMax;

	[ObservableProperty]
	private decimal? _vCountMin;

	[ObservableProperty]
	private decimal? _vCountMax;

	[ObservableProperty]
	private DateTimeOffset? _date;

	[ObservableProperty]
	private DateTimeOffset _minYear = new(new(2000, 1, 1));

	[ObservableProperty]
	private DateTimeOffset _maxYear = new(new(2099, 12, 31));

	[ObservableProperty]
	private decimal? _hour;

	[ObservableProperty]
	private decimal? _minute;

	[ObservableProperty]
	private decimal? _second;

	[ObservableProperty]
	private string? _currentMessage;

	private enum GameVersion
	{
		Black,
		White,
	}

	private record SeedParameters(ushort LowerMac, uint UpperMac, GameVersion GameVersion,
		uint VCountMin, uint VCountMax, uint VFrameMin, uint VFrameMax, DateTimeOffset Date, uint Hour, uint Minute, uint Second);

	private SeedParameters CollectParameters()
	{
		if (string.IsNullOrEmpty(MacAddress) || MacAddress.Contains('_'))
		{
			throw new("MAC Address is not fully set");
		}

		var macAddressParts = MacAddress.Split(':');
		Span<byte> macAddressBytes = stackalloc byte[6];
		for (var i = 0; i < 6; i++)
		{
			if (!byte.TryParse(macAddressParts[i], NumberStyles.HexNumber, null, out macAddressBytes[i]))
			{
				throw new("MAC Address is invalid");
			}
		}

		if (!Enum.IsDefined((GameVersion)VersionSelection))
		{
			throw new("Version is not selected");
		}

		if (!VFrameMin.HasValue)
		{
			throw new("VFrame Min is invalid");
		}

		if (!VFrameMax.HasValue)
		{
			throw new("VFrame Max is invalid");
		}

		if (!VCountMin.HasValue)
		{
			throw new("VCount Min is invalid");
		}

		if (!VCountMax.HasValue)
		{
			throw new("VCount Max is invalid");
		}

		if (!Date.HasValue)
		{
			throw new("Date is not set");
		}

		if (!Hour.HasValue)
		{
			throw new("Hour is not set");
		}

		if (!Minute.HasValue)
		{
			throw new("Minute is not set");
		}

		if (!Second.HasValue)
		{
			throw new("Second is not set");
		}

		var lowerMac = (ushort)(macAddressBytes[4] | (macAddressBytes[5] << 8));
		var upperMac = (uint)(macAddressBytes[0] | (macAddressBytes[1] << 8) | (macAddressBytes[2] << 16) | (macAddressBytes[3] << 24));
		return new(lowerMac, upperMac, (GameVersion)VersionSelection, (uint)VCountMin.Value, (uint)VCountMax.Value,
			(uint)VFrameMin.Value, (uint)VFrameMax.Value, Date.Value, (uint)Hour.Value, (uint)Minute.Value, (uint)Second.Value);
	}

	private static readonly byte[] _blackNazos =
	[
		0x20, 0x36, 0xFE, 0x02,
		0x00, 0x00, 0x00, 0x00,
		0xE0, 0x9B, 0x26, 0x02,
		0x74, 0x9D, 0x26, 0x02,
		0x74, 0x9D, 0x26, 0x02,
	];

	private static readonly byte[] _whiteNazos =
	[
		0x20, 0x36, 0xFE, 0x02,
		0x00, 0x00, 0x00, 0x00,
		0x00, 0x9C, 0x26, 0x02,
		0x94, 0x9D, 0x26, 0x02,
		0x94, 0x9D, 0x26, 0x02,
	];

	private static readonly byte[] _bcdValues = new byte[100];

	static MainViewModel()
	{
		for (var i = 0; i < _bcdValues.Length; i++)
		{
			_bcdValues[i] = (byte)(((i / 10) << 4) + i % 10);
		}
	}

	private static readonly byte[] _sha1MsgConstants =
	[
		0x00, 0x00, 0x00, 0x00, // mic data, should always be 0s
		0x00, 0x06, 0x00, 0x00, // touch inputs, only constant assuming no touch inputs
		0xFF, 0x2F, 0x00, 0x00, // keypad inputs, only constant assuming no keypad inputs
		0x80, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x01, 0xA0,
	];

	public readonly record struct SeedState(ulong Seed, ushort Timer0, ushort VCount, uint GxStat, uint TickCount, uint VFrame, DateTime DateTime);
	private record ComputeSeedThreadParam(SeedParameters SeedParameters, uint Timer0Start, uint Timer0End, ConcurrentBag<SeedState> ComputedSeeds, List<BattleTurnOutcome> BattleTurnOutcomes);
	private const uint MAX_SEEDS_ALLOWED = 165121308; // kind of arbitrary, but need to avoid running out of memory (this is the amount of seeds available in 1 vframe with 0 moves)

	private static void ComputeSeedThreadProc(object? threadParam)
	{
		var param = (ComputeSeedThreadParam)threadParam!;
		Span<byte> sha1Hash = stackalloc byte[160];
		Span<byte> sha1Msg = stackalloc byte[16 * 4];

		_sha1MsgConstants.CopyTo(sha1Msg[(10 * 4)..]);

		switch (param.SeedParameters.GameVersion)
		{
			case GameVersion.Black:
				_blackNazos.CopyTo(sha1Msg);
				break;
			case GameVersion.White:
				_whiteNazos.CopyTo(sha1Msg);
				break;
			default:
				throw new InvalidOperationException();
		}

		for (var vframe = param.SeedParameters.VFrameMin; vframe <= param.SeedParameters.VFrameMax; vframe++)
		{
			for (var vcount = param.SeedParameters.VCountMin; vcount <= param.SeedParameters.VCountMax; vcount++)
			{
				for (var timer0 = param.Timer0Start; timer0 < param.Timer0End; timer0++)
				{
					var entropyData0 = (vcount << 16) | timer0;
					BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(5 * 4)..], entropyData0);

					// each vframe takes 560190 cpu cycles
					// every 64 cpu cycles, timer0 increments
					// timer0 overflows after 65536 increments (16 bit counter)
					// OSi_TickCounter is the amount of overflows, so increments every 4194304 cpu cycles
					var presumedTickCount = (uint)((ulong)vframe * 560190 / 4194304);
					// try to avoid potential over/undershooting here
					for (var tickCount = presumedTickCount == 0 ? 0 : presumedTickCount - 1; tickCount <= presumedTickCount + 1; tickCount++)
					{
						var entropyData1 = (uint)(param.SeedParameters.LowerMac << 16) ^ tickCount;
						BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(6 * 4)..], entropyData1);

						//for (var i = 0; i < 2; i++)
						//{
							// assuming emulator is correct, there should only be 2 gxstat values possible here
							// case 1: 0x86000000
							// case 2: 0x86000002
							var gxStat = 0x86000000;//i == 0 ? 0x86000000 : 0x86000002;
							var entropyData2 = param.SeedParameters.UpperMac ^ vframe ^ gxStat;
							BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(7 * 4)..], entropyData2);

							for (var j = 0; j < 3; j++)
							{
								// stolen from pokefinder
								static byte ComputeWeekday(int year, int month, int day)
								{
									var a = month < 3 ? 1 : 0;
									var y = 2000 + year + 4800 - a;
									var m = month + 12 * a - 3;
									var jd = day + (153 * m + 2) / 5 - 32045 + 365 * y + y / 4 - y / 100 + y / 400;
									return (byte)((jd + 1) % 7);
								}

								var secondsOffset = (int)((ulong)vframe * 560190 / 33513982) - 1;
								secondsOffset += j;

								var dateTime = new DateTime(param.SeedParameters.Date.Year, param.SeedParameters.Date.Month, param.SeedParameters.Date.Day,
									(int)param.SeedParameters.Hour, (int)param.SeedParameters.Minute, (int)param.SeedParameters.Second).AddSeconds(secondsOffset);

								var year = _bcdValues[dateTime.Year - 2000];
								var month = _bcdValues[dateTime.Month];
								var day = _bcdValues[dateTime.Day];
								var weekDay = ComputeWeekday(dateTime.Year - 2000, dateTime.Month, dateTime.Day);
								var entropyData3 = (uint)((weekDay << 24) | (day << 16) | (month << 8) | year);
								BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(8 * 4)..], entropyData3);

								var hour = _bcdValues[dateTime.Hour];
								if (dateTime.Hour >= 12)
								{
									hour |= 0x40;
								}

								var minute = _bcdValues[dateTime.Minute];
								var second = _bcdValues[dateTime.Second];
								var entropyData4 = (uint)((second << 16) | (minute << 8) | hour);
								BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(9 * 4)..], entropyData4);

								PkmnSHA1.HashBlock(sha1Msg, sha1Hash);
								var seed = BinaryPrimitives.ReadUInt64LittleEndian(sha1Hash);

								var allBattleTurnsSatified = true;
								// ReSharper disable once ForCanBeConvertedToForeach
								// ReSharper disable once LoopCanBeConvertedToQuery
								var numTurns = param.BattleTurnOutcomes.Count;
								var battleTurnOutcomes = param.BattleTurnOutcomes;
								for (var k = 0; k < numTurns; k++)
								{
									if (!CheckBattleRng(ref seed, battleTurnOutcomes[k]))
									{
										allBattleTurnsSatified = false;
										break;
									}
								}

								if (!allBattleTurnsSatified)
								{
									continue;
								}

								var seedState = new SeedState(seed, (ushort)timer0, (ushort)vcount, gxStat, tickCount, vframe, dateTime);
								param.ComputedSeeds.Add(seedState);
								if (param.ComputedSeeds.Count >= MAX_SEEDS_ALLOWED)
								{
									return;
								}
							}
						//}
					}
				}
			}
		}
	}

	public void ComputeSeeds()
	{
		SeedParameters parameters;
		try
		{
			parameters = CollectParameters();
		}
		catch (Exception e)
		{
			CurrentMessage = e.Message;
			return;
		}

		_computedSeeds = [];
		var maxParallelism = Environment.ProcessorCount * 3 / 4;
		var threads = new Thread[maxParallelism];
		var timer0PerFrame = 0x10000 / (uint)maxParallelism;
		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i] = new Thread(ComputeSeedThreadProc) { IsBackground = true };
			var threadParam = new ComputeSeedThreadParam(parameters, (uint)(i * timer0PerFrame), (uint)((i + 1) * timer0PerFrame), _computedSeeds, _battleTurnOutcomes);
			threads[i].Start(threadParam);
		}

		// last couple of vcounts covered here
		{
			var threadParam = new ComputeSeedThreadParam(parameters, (uint)maxParallelism * timer0PerFrame, 0x10000, _computedSeeds, _battleTurnOutcomes);
			ComputeSeedThreadProc(threadParam);
		}

		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i].Join();
		}

		if (_computedSeeds.Count >= MAX_SEEDS_ALLOWED)
		{
			_computedSeeds = [];
			GC.Collect();
			CurrentMessage = "Too many seeds to compute, add more moves";
			return;
		}

		if (_computedSeeds.IsEmpty)
		{
			CurrentMessage = "Could not determine RNG seed (wrong VFrame window?)";
			return;
		}

		if (_computedSeeds.Count == 1)
		{
			_ = _computedSeeds.TryPeek(out var seedState);
			var seed = seedState.Seed;
			for (var i = 0; i < _numBattleRngRolls; i++)
			{
				ReverseBattleRng(ref seed);
			}

			CurrentMessage = $"Found Initial Seed {seed:X016}, Current Seed State: Seed {seedState.Seed:X016}, Timer0 {seedState.Timer0:X04}, " +
			                 $"VCount {seedState.VCount:X03}, GxStat {seedState.GxStat:X08}, TickCount {seedState.TickCount:X}, " +
			                 $"VFrame {seedState.VFrame}, DateTime {seedState.DateTime.ToString(CultureInfo.InvariantCulture)}";
			return;
		}

		CurrentMessage = $"Computed {_computedSeeds.Count} seeds";
	}

	private ConcurrentBag<SeedState> _computedSeeds = [];

	private enum PlayerTurnOutcome : byte
	{
		SupersonicHit,
		SupersonicMiss,
		GrowlHit,
	}

	private enum EnemyTurnOutcome : byte
	{
		ConfusionSelfHit,
		LeerHit,
		TackleHit,
		TackleHitCrit,
		OdorSleuth,
	}

	private readonly record struct BattleTurnOutcome(
		PlayerTurnOutcome PlayerTurnOutcome, bool QuickClawActivated,
		EnemyTurnOutcome EnemyTurnOutcome, bool SnappedOutOfConfusion,
		int TurnsSinceConfusion, int RngRollsSinceConfusion);

	private List<BattleTurnOutcome> _battleTurnOutcomes = [];
	private int _turnsSinceConfusion;
	private int _battleRngRollConfusionTriggerNum;
	private int _numBattleRngRolls;

	[ObservableProperty]
	private int _playerTurnOutcomeSelection;

	[ObservableProperty]
	private bool _quickClawActivated;

	[ObservableProperty]
	private int _enemyTurnOutcomeSelection;

	[ObservableProperty]
	private bool _snappedOutOfConfusion;

	private record AddTurnThreadParam(ConcurrentBag<SeedState> ComputedSeeds, ConcurrentBag<SeedState> NextComputedSeeds, BattleTurnOutcome BattleTurnOutcome);

	private static bool CheckBattleRng(ref ulong battleRng, BattleTurnOutcome battleTurnOutcome)
	{
		var quickClawCheck = (int)((AdvanceBattleRng(ref battleRng) * 100) >> 32);
		var quickClawActivated = quickClawCheck < 20;

		if (quickClawActivated != battleTurnOutcome.QuickClawActivated)
		{
			return false;
		}

		switch (battleTurnOutcome.PlayerTurnOutcome)
		{
			case PlayerTurnOutcome.SupersonicHit or PlayerTurnOutcome.SupersonicMiss:
			{
				var accCheckRoll = (int)((AdvanceBattleRng(ref battleRng) * 100) >> 32);
				var accCheckHit = accCheckRoll < 55;
				var wantHit = battleTurnOutcome.PlayerTurnOutcome == PlayerTurnOutcome.SupersonicHit;
				if (accCheckHit != wantHit)
				{
					return false;
				}

				if (accCheckHit)
				{
					AdvanceBattleRng(ref battleRng); // confusion turn count
				}

				break;
			}

			case PlayerTurnOutcome.GrowlHit:
				// acc check (100%, don't care)
				AdvanceBattleRng(ref battleRng);
				break;
		}

		if (battleTurnOutcome.SnappedOutOfConfusion)
		{
			var confusionTurnCountRng = battleRng;
			for (var i = 0; i < battleTurnOutcome.RngRollsSinceConfusion; i++)
			{
				ReverseBattleRng(ref confusionTurnCountRng);
			}

			var confusionTurnCount = (int)((AdvanceBattleRng(ref confusionTurnCountRng) * 4) >> 32);
			confusionTurnCount += 2;
			if (confusionTurnCount != battleTurnOutcome.TurnsSinceConfusion)
			{
				return false;
			}
		}
		else if (battleTurnOutcome.TurnsSinceConfusion > 0)
		{
			var selfHitCheckRoll = (int)((AdvanceBattleRng(ref battleRng) * 100) >> 32);
			var gotSelfHit = selfHitCheckRoll < 50;
			var wantSelfHit = battleTurnOutcome.EnemyTurnOutcome == EnemyTurnOutcome.ConfusionSelfHit;
			if (gotSelfHit != wantSelfHit)
			{
				return false;
			}
		}

		switch (battleTurnOutcome.EnemyTurnOutcome)
		{
			case EnemyTurnOutcome.ConfusionSelfHit:
			{
				AdvanceBattleRng(ref battleRng); // damage roll (don't care)
				break;
			}

			case EnemyTurnOutcome.LeerHit:
			{
				AdvanceBattleRng(ref battleRng); // accuracy roll (don't care)
				break;
			}

			case EnemyTurnOutcome.TackleHit:
			case EnemyTurnOutcome.TackleHitCrit:
			{
				AdvanceBattleRng(ref battleRng); // accuracy roll (don't care)
				var critCheckRoll = (int)((AdvanceBattleRng(ref battleRng) * 16) >> 32); // crit roll
				var gotCrit = critCheckRoll == 0;
				var wantCrit = battleTurnOutcome.EnemyTurnOutcome == EnemyTurnOutcome.TackleHitCrit;
				if (gotCrit != wantCrit)
				{
					return false;
				}

				AdvanceBattleRng(ref battleRng); // damage roll (don't care)
				break;
			}

			case EnemyTurnOutcome.OdorSleuth:
				// no rng calls
				break;

			default:
				throw new InvalidOperationException();
		}

		return true;
	}

	private static void AddTurnThreadProc(object? threadParam)
	{
		var param = (AddTurnThreadParam)threadParam!;
		while (param.ComputedSeeds.TryTake(out var computedSeedState))
		{
			var seed = computedSeedState.Seed;
			if (!CheckBattleRng(ref seed, param.BattleTurnOutcome))
			{
				continue;
			}

			param.NextComputedSeeds.Add(computedSeedState with { Seed = seed });
		}
	}

	public void AddTurn()
	{
		if (!Enum.IsDefined((PlayerTurnOutcome)PlayerTurnOutcomeSelection))
		{
			CurrentMessage = "Player Turn Outcome not set";
			return;
		}

		if (!Enum.IsDefined((EnemyTurnOutcome)EnemyTurnOutcomeSelection))
		{
			CurrentMessage = "Player Turn Outcome not set";
			return;
		}

		var playerTurnOutcome = (PlayerTurnOutcome)PlayerTurnOutcomeSelection;
		var enemyTurnOutcome = (EnemyTurnOutcome)EnemyTurnOutcomeSelection;

		if (enemyTurnOutcome == EnemyTurnOutcome.ConfusionSelfHit && SnappedOutOfConfusion)
		{
			CurrentMessage = "Can't snap out of confusion while self hitting";
			return;
		}

		if (_turnsSinceConfusion == 0 && SnappedOutOfConfusion)
		{
			CurrentMessage = "Can't snap out of confusion without confusion inflicted a previous turn";
			return;
		}

		_numBattleRngRolls++; // quick claw rng roll
		_numBattleRngRolls += playerTurnOutcome switch
		{
			PlayerTurnOutcome.SupersonicHit => 2, // 1 acc check, 1 confusion turn count roll
			PlayerTurnOutcome.SupersonicMiss => 1, // 1 acc check
			PlayerTurnOutcome.GrowlHit => 1, // 1 acc check
			_ => throw new InvalidOperationException()
		};

		switch (_turnsSinceConfusion)
		{
			case 0 when playerTurnOutcome == PlayerTurnOutcome.SupersonicHit:
				_battleRngRollConfusionTriggerNum = _numBattleRngRolls - 1;
				_turnsSinceConfusion++;
				break;
			case > 0:
				_turnsSinceConfusion++;
				break;
		}

		var rngRollsSinceConfusion = _numBattleRngRolls - _battleRngRollConfusionTriggerNum;
		var battleTurnOutcome = new BattleTurnOutcome(playerTurnOutcome, QuickClawActivated, enemyTurnOutcome, SnappedOutOfConfusion, _turnsSinceConfusion, rngRollsSinceConfusion);
		_battleTurnOutcomes.Add(battleTurnOutcome);

		if (SnappedOutOfConfusion)
		{
			_turnsSinceConfusion = 0;
		}

		if (_turnsSinceConfusion > 0)
		{
			_numBattleRngRolls++; // confusion self hit check
		}

		_numBattleRngRolls += enemyTurnOutcome switch
		{
			EnemyTurnOutcome.ConfusionSelfHit => 1, // 1 damage roll
			EnemyTurnOutcome.LeerHit => 1, // 1 acc check
			EnemyTurnOutcome.TackleHit or EnemyTurnOutcome.TackleHitCrit => 3, // 1 acc check, 1 crit check, 1 damage roll
			EnemyTurnOutcome.OdorSleuth => 0, // oder sleuth doesn't produce any rng calls (bypasses acc checks)
			_ => throw new InvalidOperationException()
		};

		if (_computedSeeds.IsEmpty)
		{
			CurrentMessage = $"Added Battle Turn Outcome, {_battleTurnOutcomes.Count} turn outcomes currently set";
			return;
		}

		var maxParallelism = Environment.ProcessorCount * 3 / 4;
		var threads = new Thread[maxParallelism];
		var nextComputedSeeds = new ConcurrentBag<SeedState>();
		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i] = new Thread(AddTurnThreadProc) { IsBackground = true };
			var threadParam = new AddTurnThreadParam(_computedSeeds, nextComputedSeeds, battleTurnOutcome);
			threads[i].Start(threadParam);
		}

		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i].Join();
		}

		_computedSeeds = nextComputedSeeds;
		if (_computedSeeds.IsEmpty)
		{
			CurrentMessage = "Could not determine RNG seed (wrong VFrame window?)";
			return;
		}

		if (_computedSeeds.Count == 1)
		{
			_ = _computedSeeds.TryPeek(out var seedState);
			var seed = seedState.Seed;
			for (var i = 0; i < _numBattleRngRolls; i++)
			{
				ReverseBattleRng(ref seed);
			}

			CurrentMessage = $"Found Initial Seed {seed:X016}, Current Seed State: Seed {seedState.Seed:X016}, Timer0 {seedState.Timer0:X04}, " +
			                 $"VCount {seedState.VCount:X03}, GxStat {seedState.GxStat:X08}, TickCount {seedState.TickCount:X}, " +
			                 $"VFrame {seedState.VFrame}, DateTime {seedState.DateTime.ToString(CultureInfo.InvariantCulture)}";
			return;
		}

		CurrentMessage = $"{_computedSeeds.Count} seeds remaining";
	}

	public void CheckAnySeedUnique()
	{
		// nothing to do if there's less than 2 seeds
		if (_computedSeeds.Count < 2)
		{
			return;
		}

		var takenComputedSeeds = new ConcurrentBag<SeedState>();
		_ = _computedSeeds.TryPeek(out var exSeedState);
		var exSeed = exSeedState.Seed;
		var anySeedUnique = false;
		while (_computedSeeds.TryTake(out var seedState))
		{
			takenComputedSeeds.Add(seedState);

			if (seedState.Seed != exSeed)
			{
				anySeedUnique = true;
				break;
			}
		}

		if (anySeedUnique)
		{
			while (takenComputedSeeds.TryTake(out var seedState))
			{
				_computedSeeds.Add(seedState);
			}

			CurrentMessage = "Unique seeds are still present";
			return;
		}

		var message = "All Seeds are identical";
		while (takenComputedSeeds.TryTake(out var seedState))
		{
			var seed = seedState.Seed;
			for (var i = 0; i < _numBattleRngRolls; i++)
			{
				ReverseBattleRng(ref seed);
			}

			message += $"\r\nInitial Seed {seed:X016}, Current Seed State: Seed {seedState.Seed:X016}, Timer0 {seedState.Timer0:X04}, " +
			           $"VCount {seedState.VCount:X03}, GxStat {seedState.GxStat:X08}, TickCount {seedState.TickCount:X}, " +
			           $"VFrame {seedState.VFrame}, DateTime {seedState.DateTime.ToString(CultureInfo.InvariantCulture)}";

			_computedSeeds.Add(seedState);
		}

		CurrentMessage = message;
	}

	public void Reset()
	{
		_computedSeeds = [];
		_battleTurnOutcomes = [];
		_numBattleRngRolls = 0;
		_turnsSinceConfusion = 0;
		_battleRngRollConfusionTriggerNum = 0;
		GC.Collect();
		CurrentMessage = "Seeds/Turns reset";
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ulong AdvanceBattleRng(ref ulong pidRng)
	{
		pidRng *= 0x5d588b656c078965;
		pidRng += 0x269ec3;
		return pidRng >> 32;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void ReverseBattleRng(ref ulong pidRng)
	{
		pidRng *= 0xdedcedae9638806d;
		pidRng += 0x9b1ae6e9a384e6f9;
	}
}
