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

	private record SeedParameters(ushort LowerMac, uint UpperMac, GameVersion GameVersion, uint VFrameMin, uint VFrameMax, DateTimeOffset Date, uint Hour, uint Minute, uint Second);

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
		return new(lowerMac, upperMac, (GameVersion)VersionSelection, (uint)VFrameMin.Value, (uint)VFrameMax.Value, Date.Value, (uint)Hour.Value, (uint)Minute.Value, (uint)Second.Value);
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

	public readonly record struct SeedState(ulong Seed, uint Timer0, uint VCount, uint VFrame, DateTime DateTime);
	private record ComputeSeedThreadParam(SeedParameters SeedParameters, uint VCountStart, uint VCountEnd, ConcurrentBag<SeedState> ComputedSeeds, List<BattleTurnOutcome> BattleTurnOutcomes);
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
			for (var vcount = param.VCountStart; vcount < param.VCountEnd; vcount++)
			{
				// each vframe takes 560190 cpu cycles
				// every 64 cpu cycles, timer0 increments
				var presumedTimer0 = (uint)((ulong)vframe * 560190 / 64);
				var lowerEndTimer0 = (presumedTimer0 - (560190 / 64 + 1) * 2) & 0xFFFF;
				const uint timer0Variance = (560190 / 64 + 1) * 4;

				for (var timer0Offset = 0u; timer0Offset <= timer0Variance; timer0Offset++)
				{
					var timer0 = (lowerEndTimer0 + timer0Offset) & 0xFFFF;
					var entropyData0 = (vcount << 16) | timer0;
					BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(5 * 4)..], entropyData0);

					// timer0 overflows after 65536 increments (16 bit counter)
					// OSi_TickCounter is the amount of overflows, so increments every 4194304 cpu cycles
					var presumedTickCount = (uint)((ulong)vframe * 560190 / 4194304);
					// try to avoid potential over/undershooting here
					for (var tickCount = presumedTickCount == 0 ? 0 : presumedTickCount - 1; tickCount <= presumedTickCount + 1; tickCount++)
					{
						var entropyData1 = (uint)(param.SeedParameters.LowerMac << 16) ^ tickCount;
						BinaryPrimitives.WriteUInt32LittleEndian(sha1Msg[(6 * 4)..], entropyData1);

						for (var i = 0; i < 2; i++)
						{
							// assuming emulator is correct, there should only be 2 gxstat values possible here
							// case 1: 0x86000000
							// case 2: 0x86000002
							var gxStat = i == 0 ? 0x86000000 : 0x86000002;
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

								var accuracyStage = 0;
								var accuracy = 0;
								var allBattleTurnsSatified = true;
								foreach (var battleTurnOutcome in param.BattleTurnOutcomes)
								{
									if (accuracyStage > -6)
									{
										accuracyStage--;
										accuracy = accuracyStage switch
										{
											-1 => 75,
											-2 => 60,
											-3 => 50,
											-4 => 42,
											-5 => 37,
											-6 => 33,
											_ => throw new InvalidOperationException(),
										};
									}

									AdvanceBattleRng(ref seed); // sand attack acc check (don't care)

									if (battleTurnOutcome == BattleTurnOutcome.OdorSleuth)
									{
										// no acc check here
										continue;
									}

									var accCheck = (int)((AdvanceBattleRng(ref seed) * 100) >> 32);
									var accCheckHit = accCheck < accuracy;
									var wantHit = battleTurnOutcome is BattleTurnOutcome.LeerHit or BattleTurnOutcome.TackleHit or BattleTurnOutcome.TackleHitCrit;
									if (accCheckHit != wantHit)
									{
										allBattleTurnsSatified = false;
										break;
									}

									if (battleTurnOutcome is BattleTurnOutcome.TackleHit or BattleTurnOutcome.TackleHitCrit)
									{
										var critCheck = (int)((AdvanceBattleRng(ref seed) * 16) >> 32);
										var gotCrit = critCheck == 0;
										var wantCrit = battleTurnOutcome == BattleTurnOutcome.TackleHitCrit;
										if (gotCrit != wantCrit)
										{
											allBattleTurnsSatified = false;
											break;
										}

										AdvanceBattleRng(ref seed); // damage roll (don't care)
									}
								}

								if (!allBattleTurnsSatified)
								{
									continue;
								}

								var seedState = new SeedState(seed, timer0, vcount, vframe, dateTime);
								param.ComputedSeeds.Add(seedState);
								if (param.ComputedSeeds.Count >= MAX_SEEDS_ALLOWED)
								{
									return;
								}
							}
						}
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
		var vCountPerThread = 263 / (uint)maxParallelism;
		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i] = new Thread(ComputeSeedThreadProc) { IsBackground = true };
			var threadParam = new ComputeSeedThreadParam(parameters, (uint)(i * vCountPerThread), (uint)((i + 1) * vCountPerThread), _computedSeeds, _battleTurnOutcomes);
			threads[i].Start(threadParam);
		}

		// last couple of vcounts covered here
		{
			var threadParam = new ComputeSeedThreadParam(parameters, (uint)maxParallelism * vCountPerThread, 262, _computedSeeds, _battleTurnOutcomes);
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

		CurrentMessage = $"Computed {_computedSeeds.Count} seeds";
	}

	private ConcurrentBag<SeedState> _computedSeeds = [];

	private enum BattleTurnOutcome
	{
		LeerMiss,
		TackleMiss,
		LeerHit,
		TackleHit,
		TackleHitCrit,
		OdorSleuth,
	}

	private List<BattleTurnOutcome> _battleTurnOutcomes = [];
	private int _currentAccuracyStage;
	private int _numBattleRngRolls;

	[ObservableProperty]
	private int _battleTurnOutcomeSelection;

	private record AddMoveThreadParam(ConcurrentBag<SeedState> ComputedSeeds, ConcurrentBag<SeedState> NextComputedSeeds, BattleTurnOutcome BattleTurnOutcome, int Accuracy);

	private static void AddMoveThreadProc(object? threadParam)
	{
		var param = (AddMoveThreadParam)threadParam!;
		while (param.ComputedSeeds.TryTake(out var computedSeedState))
		{
			var seed = computedSeedState.Seed;
			AdvanceBattleRng(ref seed); // sand attack acc check (don't care)
			if (param.BattleTurnOutcome == BattleTurnOutcome.OdorSleuth)
			{
				// no more rng calls if we got oder sleuth
				param.NextComputedSeeds.Add(computedSeedState with { Seed = seed });
				continue;
			}

			var accCheck = (int)((AdvanceBattleRng(ref seed) * 100) >> 32);
			var accCheckHit = accCheck < param.Accuracy;
			var wantHit = param.BattleTurnOutcome is BattleTurnOutcome.LeerHit or BattleTurnOutcome.TackleHit or BattleTurnOutcome.TackleHitCrit;
			if (accCheckHit != wantHit)
			{
				continue;
			}

			if (param.BattleTurnOutcome is BattleTurnOutcome.TackleHit or BattleTurnOutcome.TackleHitCrit)
			{
				var critCheck = (int)((AdvanceBattleRng(ref seed) * 16) >> 32);
				var gotCrit = critCheck == 0;
				var wantCrit = param.BattleTurnOutcome == BattleTurnOutcome.TackleHitCrit;
				if (gotCrit != wantCrit)
				{
					continue;
				}

				AdvanceBattleRng(ref seed); // damage roll (don't care)
			}

			param.NextComputedSeeds.Add(computedSeedState with { Seed = seed });
		}
	}

	public void AddMove()
	{
		if (!Enum.IsDefined((BattleTurnOutcome)BattleTurnOutcomeSelection))
		{
			CurrentMessage = "Battle Turn Outcome not set";
			return;
		}

		var battleTurnOutcome = (BattleTurnOutcome)BattleTurnOutcomeSelection;
		_battleTurnOutcomes.Add(battleTurnOutcome);
		_numBattleRngRolls += battleTurnOutcome switch
		{
			BattleTurnOutcome.LeerMiss or BattleTurnOutcome.TackleMiss or BattleTurnOutcome.LeerHit => 2,
			BattleTurnOutcome.TackleHit or BattleTurnOutcome.TackleHitCrit => 4,
			BattleTurnOutcome.OdorSleuth => 1, // oder sleuth doesn't produce any rng calls (always succeeds)
			_ => throw new InvalidOperationException()
		};

		if (_currentAccuracyStage > -6)
		{
			_currentAccuracyStage--;
		}

		if (_computedSeeds.IsEmpty)
		{
			CurrentMessage = $"Added Battle Turn Outcome {battleTurnOutcome}, {_battleTurnOutcomes.Count} turn outcomes currently set";
			return;
		}

		var accuracy = _currentAccuracyStage switch
		{
			-1 => 75,
			-2 => 60,
			-3 => 50,
			-4 => 42,
			-5 => 37,
			-6 => 33,
			_ => throw new InvalidOperationException(),
		};

		var maxParallelism = Environment.ProcessorCount * 3 / 4;
		var threads = new Thread[maxParallelism];
		var nextComputedSeeds = new ConcurrentBag<SeedState>();
		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i] = new Thread(AddMoveThreadProc) { IsBackground = true };
			var threadParam = new AddMoveThreadParam(_computedSeeds, nextComputedSeeds, battleTurnOutcome, accuracy);
			threads[i].Start(threadParam);
		}

		for (var i = 0; i < maxParallelism; i++)
		{
			threads[i].Join();
		}

		_computedSeeds = nextComputedSeeds;
		if (_computedSeeds.IsEmpty)
		{
			GC.Collect();
			CurrentMessage = "Could not determine RNG seed (wrong VFrame window?)";
			return;
		}

		if (_computedSeeds.Count == 1)
		{
			_ = _computedSeeds.TryTake(out var seedState);
			var seed = seedState.Seed;
			for (var i = 0; i < _numBattleRngRolls; i++)
			{
				ReverseBattleRng(ref seed);
			}

			CurrentMessage = $"Found Initial Seed {seed:X016}, Current Seed State: Seed {seedState.Seed:X016}, Timer0 {seedState.Timer0:X04}, " +
			                 $"VCount {seedState.VCount:X03}, VFrame {seedState.VFrame}, DateTime {seedState.DateTime.ToString(CultureInfo.InvariantCulture)}";
			return;
		}

		CurrentMessage = $"{_computedSeeds.Count} seeds remaining";
	}

	public void Reset()
	{
		_computedSeeds = [];
		_battleTurnOutcomes = [];
		_numBattleRngRolls = 0;
		_currentAccuracyStage = 0;
		GC.Collect();
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
