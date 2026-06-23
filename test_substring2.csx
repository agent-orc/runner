// Check if the substring logic is actually a problem
string[] models = { "gpt-5.5", "gpt-5-5", "gpt-5-5-test", "gpt-5-50", "gpt-5", "gpt-6", "gpt-7", "gpt-500" };

foreach (var model in models)
{
    var m = model.Replace('.', '-').ToLowerInvariant();
    var result = m.Contains("gpt-5-5", StringComparison.Ordinal) 
                 || m.Contains("gpt-6", StringComparison.Ordinal)
                 || m.Contains("gpt-7", StringComparison.Ordinal);
    Console.WriteLine($"Model '{model}' → '{m}' → IsXHigh: {result}");
}
