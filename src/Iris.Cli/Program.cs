using Iris.CodingAgent.Core.Extensions;
using Iris.WebAccess;

BuiltinExtensions.Register(WebAccessExtension.Id, () => new WebAccessExtension());
return await Iris.CodingAgent.Cli.Main.RunAsync(args);
