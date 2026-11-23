using BinScope.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
return CliApp.Run(args, new CliIo(Console.Out, File.ReadAllBytes, File.WriteAllBytes));
