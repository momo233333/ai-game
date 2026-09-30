using System;
using System.Reflection;

namespace Copilot.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string filter = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[i + 1];
                else if (args[i] == "--no-server") Environment.SetEnvironmentVariable("COPILOT_SKIP_SERVER", "1");
                else if (args[i] == "--require-server") ServerIntegrationTests.RequireServer = true;
                else if (args[i] == "--help")
                {
                    Console.WriteLine("usage: dotnet run [-- --filter <name>] [--no-server] [--require-server]");
                    return 0;
                }
            }

            try
            {
                return TestRunner.RunAll(Assembly.GetExecutingAssembly(), filter);
            }
            finally
            {
                ServerIntegrationTests.Shutdown();
            }
        }
    }
}
