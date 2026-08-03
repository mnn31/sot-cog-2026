// ReplayRecorder: a standalone dev tool (NOT part of the scripts-tribute repo deliverable).
//
// Investigation finding: GameRunner's `-l BOTH -d <dir>` per-player text logs only contain
// whatever a bot chooses to write via its own protected `Log(string)` call (see
// ScriptsOfTribute-Core/Engine/src/AI/AI.cs + ScriptsOfTributeGame.cs). The engine itself
// never auto-logs moves. Riptide and BestMCTS3 only call Log() on error/recovery paths, so a
// real `Riptide vs BestMCTS3` run produces EMPTY *_p1.log / *_p2.log files (verified: ran
// seed 12345, both files were 0 bytes). Only a few bots (e.g. MaxPrestigeBot) opt in to
// per-move logging via `Log(move.ToString())`.
//
// Since the engine already tracks everything we need internally (GameState.SerializeGameState(),
// which surfaces CompletedActions / patron favor / resources / hands / agents in fair,
// non-hidden-info form since Coins/Power/Prestige/Agents are public knowledge), this harness
// hooks the *public* AI.Play()/SelectPatron()/GameEnd() interface with a recording decorator
// and dumps a JSON trace. This uses only the existing public engine API - no engine/bot source
// was modified. The resulting JSON trace is what scripts/replay.py actually consumes.

using System.Reflection;
using Newtonsoft.Json.Linq;
using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: ReplayRecorder <bot1> <bot2> <seed> <timeoutSeconds> <outputJsonPath>");
    return 1;
}

string bot1Name = args[0];
string bot2Name = args[1];
ulong seed = ulong.Parse(args[2]);
int timeout = int.Parse(args[3]);
string outputPath = args[4];

var botsAssembly = Assembly.LoadFrom("/Users/manan/WORK/COG/scripts-tribute/ScriptsOfTribute-Core/Bots/bin/Release/netstandard2.1/Bots.dll");
var aiType = typeof(AI);
var allBotTypes = botsAssembly.GetTypes().Where(t => aiType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract).ToList();

AI Instantiate(string name)
{
    var matches = allBotTypes.Where(t => t.Name == name || t.FullName == name).ToList();
    if (matches.Count == 0)
    {
        Console.Error.WriteLine($"Bot '{name}' not found. Available: {string.Join(", ", allBotTypes.Select(t => t.Name))}");
        Environment.Exit(1);
    }
    if (matches.Count > 1)
    {
        matches = matches.Where(t => t.FullName == name).ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine($"Ambiguous bot name '{name}': {string.Join(", ", allBotTypes.Where(t => t.Name == name).Select(t => t.FullName))}");
            Environment.Exit(1);
        }
    }
    return (AI)Activator.CreateInstance(matches[0])!;
}

var innerBot1 = Instantiate(bot1Name);
var innerBot2 = Instantiate(bot2Name);
innerBot1.Id = PlayerEnum.PLAYER1;
innerBot2.Id = PlayerEnum.PLAYER2;

var trace = new JArray();
var patronPicks = new JArray();
int stepIndex = 0;

var recBot1 = new RecordingAI(innerBot1, trace, patronPicks, () => stepIndex++);
var recBot2 = new RecordingAI(innerBot2, trace, patronPicks, () => stepIndex++);

var game = new ScriptsOfTribute.AI.ScriptsOfTribute(recBot1, recBot2, TimeSpan.FromSeconds(timeout), ScriptsOfTribute.AI.ScriptsOfTribute.GetDefaultPatrons())
{
    Seed = seed,
};

var (endGameState, finalState) = game.Play();

var root = new JObject
{
    ["bot1"] = bot1Name,
    ["bot2"] = bot2Name,
    ["seed"] = seed,
    ["timeoutSeconds"] = timeout,
    ["patronPicks"] = patronPicks,
    ["steps"] = trace,
    ["result"] = new JObject
    {
        ["winner"] = endGameState.Winner.ToString(),
        ["reason"] = endGameState.Reason.ToString(),
        ["additionalContext"] = endGameState.AdditionalContext,
    },
    ["finalState"] = finalState is not null ? new GameState(finalState).SerializeGameState() : null,
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, root.ToString(Newtonsoft.Json.Formatting.None));
Console.WriteLine($"Wrote trace with {trace.Count} steps to {outputPath}");
Console.WriteLine(endGameState.ToString());
return 0;

class RecordingAI : AI
{
    private readonly AI _inner;
    private readonly JArray _trace;
    private readonly JArray _patronPicks;
    private readonly Func<int> _nextIndex;

    public RecordingAI(AI inner, JArray trace, JArray patronPicks, Func<int> nextIndex)
    {
        _inner = inner;
        _trace = trace;
        _patronPicks = patronPicks;
        _nextIndex = nextIndex;
    }

    public override void PregamePrepare() => _inner.PregamePrepare();

    public override PatronId SelectPatron(List<PatronId> availablePatrons, int round)
    {
        var pick = _inner.SelectPatron(availablePatrons, round);
        _patronPicks.Add(new JObject
        {
            ["player"] = Id.ToString(),
            ["round"] = round,
            ["patron"] = pick.ToString(),
        });
        return pick;
    }

    public override Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
    {
        var move = _inner.Play(gameState, possibleMoves, remainingTime);
        var record = new JObject
        {
            ["i"] = _nextIndex(),
            ["mover"] = Id.ToString(),
            ["move"] = move.ToString(),
            ["moveJson"] = SafeToJson(move),
            ["possibleMoveCount"] = possibleMoves.Count,
            ["state"] = gameState.SerializeGameState(),
        };
        _trace.Add(record);
        return move;
    }

    private static string SafeToJson(Move move)
    {
        try { return move.ToJSON(); } catch { return move.ToString(); }
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
        _inner.GameEnd(state, finalBoardState);
    }
}
