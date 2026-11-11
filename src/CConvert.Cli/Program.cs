using System.Text;
using CConvert.Core;

// cconvert <file.cdr> [...] [--out <output folder>]
// Downgrade to CorelDRAW 2019. Never overwrite the source file: the result is <name>_2019.cdr.

Console.OutputEncoding = Encoding.UTF8;

var inputs = new List<string>();
string? outDir = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--out") outDir = args[++i];
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine("Usage: cconvert <file.cdr> [...] [--out <output folder>]");
    return 2;
}

var target = CorelVersion.V2019;
if (outDir is not null) Directory.CreateDirectory(outDir);
int failed = 0;
foreach (string input in inputs)
{
    string output = CdrInfo.OutputPathFor(input, target, outDir);
    Console.WriteLine($"=== {input}");
    var result = Downgrader.Run(input, output, target);
    foreach (var entry in result.Log)
        Console.WriteLine("  " + entry);
    Console.WriteLine(result.Success ? (result.HasWarnings ? "  → DONE (with warnings)" : "  → DONE") : "  → FAILURE");
    if (!result.Success) failed++;
}
return failed == 0 ? 0 : 1;
