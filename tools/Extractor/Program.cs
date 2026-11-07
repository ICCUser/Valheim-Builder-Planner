// Developer tool: runs the same extraction the planner does on first launch.
// Usage: Extractor [ValheimFolder] [OutputFolder]
// Defaults: the Valheim install found through Steam, and the planner's user-data folder.
using ValheimPlanner.GameData;

string? game = args.Length > 0 ? args[0] : GameExtractor.FindInstalls().FirstOrDefault();
if (game == null || !GameExtractor.IsValheimFolder(game)) { Console.Error.WriteLine("Valheim install not found"); return 1; }
string outDir = args.Length > 1 ? args[1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValheimBuilderPlanner");

Console.WriteLine($"Valheim: {game}\nOutput:  {outDir}");
var sw = System.Diagnostics.Stopwatch.StartNew();
var info = GameExtractor.Run(game, outDir, (f, step) => Console.WriteLine($"  {f,4:P0}  {step}"));
Console.WriteLine($"{info.Pieces} pieces, {info.Shapes} shapes, Unity {info.Unity}, in {sw.Elapsed.TotalSeconds:0} s");
return 0;
