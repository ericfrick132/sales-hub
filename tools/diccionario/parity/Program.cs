using System.Text.Json;
using SalesHub.Infrastructure.Services;
var rules = IntentClassifier.LoadSeed().OrderBy(r => r.SortOrder).Select(r => (r.Key, r.Pattern, r.MaxWords));
var m = new IntentMatcher(rules);
var items = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.EnumerateArray().ToList();
int same = 0; var diffs = new Dictionary<string,int>(); var ex = new List<string>();
foreach (var it in items)
{
    var cs = m.Classify(it.GetProperty("in").GetString(), it.GetProperty("prev").ValueKind == JsonValueKind.Null ? null : it.GetProperty("prev").GetString());
    var py = it.GetProperty("py").GetString();
    if (cs == py) { same++; continue; }
    var k = $"{py} -> {cs}"; diffs[k] = diffs.GetValueOrDefault(k) + 1;
    if (ex.Count < 15) ex.Add($"{k}: {it.GetProperty("in").GetString()![..Math.Min(90, it.GetProperty("in").GetString()!.Length)]}");
}
Console.WriteLine($"iguales {same}/{items.Count} ({100.0*same/items.Count:F2}%)");
foreach (var d in diffs.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {d.Value,5} {d.Key}");
ex.ForEach(Console.WriteLine);
