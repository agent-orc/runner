// Test substring behavior
var test1 = "gpt-5-5".Contains("gpt-5-5");  // Should be True
var test2 = "gpt-5-5-test".Contains("gpt-5-5"); // Should be True (substring)
var test3 = "gpt-5-50".Contains("gpt-5-5"); // Should be False (not a substring)
var test4 = "gpt-50".Contains("gpt-5-5"); // Should be False (not a substring)

Console.WriteLine($"'gpt-5-5' contains 'gpt-5-5': {test1}");
Console.WriteLine($"'gpt-5-5-test' contains 'gpt-5-5': {test2}");
Console.WriteLine($"'gpt-5-50' contains 'gpt-5-5': {test3}");
Console.WriteLine($"'gpt-50' contains 'gpt-5-5': {test4}");
