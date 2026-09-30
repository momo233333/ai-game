using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Copilot.Tests
{
    /// <summary>零依赖的迷你测试框架（不能引 NuGet，CI 与本地都直接 dotnet run）。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    public sealed class AssertFailedException : Exception
    {
        public AssertFailedException(string message) : base(message)
        {
        }
    }

    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) throw new AssertFailedException(message ?? "expected true");
        }

        public static void False(bool condition, string message = null)
        {
            if (condition) throw new AssertFailedException(message ?? "expected false");
        }

        public static void Equal(object expected, object actual, string message = null)
        {
            if (!Equals(expected, actual))
                throw new AssertFailedException(
                    (message ?? "values differ") + $" | expected=<{expected ?? "null"}> actual=<{actual ?? "null"}>");
        }

        public static void Nearly(double expected, double actual, double tolerance = 1e-4, string message = null)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new AssertFailedException(
                    (message ?? "values not nearly equal") + $" | expected={expected} actual={actual} tol={tolerance}");
        }

        public static void Throws<TException>(Action action, string message = null) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new AssertFailedException(
                    (message ?? "wrong exception") + $" | expected={typeof(TException).Name} actual={ex.GetType().Name}");
            }
            throw new AssertFailedException((message ?? "no exception") + $" | expected={typeof(TException).Name}");
        }
    }

    public static class TestRunner
    {
        public static int RunAll(Assembly assembly, string filter)
        {
            var methods = assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.DeclaringType.Name)
                .ThenBy(m => m.Name)
                .ToList();

            int passed = 0;
            var failures = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            foreach (var method in methods)
            {
                string name = method.DeclaringType.Name + "." + method.Name;
                try
                {
                    object instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType);
                    method.Invoke(instance, null);
                    passed++;
                    Console.WriteLine("  ok   " + name);
                }
                catch (TargetInvocationException tie)
                {
                    Exception ex = tie.InnerException ?? tie;
                    failures.Add(name + ": " + ex.Message);
                    Console.WriteLine("  FAIL " + name);
                    Console.WriteLine("       " + ex.Message.Replace("\n", "\n       "));
                }
            }

            stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine($"{passed}/{methods.Count} passed, {failures.Count} failed ({stopwatch.ElapsedMilliseconds} ms)");
            if (failures.Count > 0)
            {
                Console.WriteLine("failures:");
                foreach (var failure in failures) Console.WriteLine("  - " + failure);
            }
            return failures.Count == 0 ? 0 : 1;
        }
    }
}
