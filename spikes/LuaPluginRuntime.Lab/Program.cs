using MdExplorer.LuaPluginRuntime;

var fileCount = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 3;
var demoId = args.Length > 0 ? args[0] : "organize-images";
var result = LuaPluginSandbox.RunDemo(demoId, fileCount);
Console.WriteLine(result.PreviewText);
Console.WriteLine($"needs-confirmation={result.RequiresUserConfirmation}; wrote-files={result.WroteFiles}");
