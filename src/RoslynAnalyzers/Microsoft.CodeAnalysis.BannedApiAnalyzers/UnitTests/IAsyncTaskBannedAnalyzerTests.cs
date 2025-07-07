// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Testing;
using Test.Utilities;
using Xunit;

using VerifyCS = Test.Utilities.CSharpCodeFixVerifier<
    Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.CSharpIAsyncTaskBannedAnalyzer,
    Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

namespace Microsoft.CodeAnalysis.BannedApiAnalyzers.UnitTests
{
    public class IAsyncTaskBannedAnalyzerTests
    {
        private const string IAsyncTaskBannedApiFileName = "IAsyncTaskBannedApis.txt";

        private static DiagnosticResult GetCSharpResultAt(int markupKey, string bannedMemberName, string message)
            => VerifyCS.Diagnostic("RS0036")
                .WithLocation(markupKey)
                .WithArguments(bannedMemberName, message);

        private static async Task VerifyCSharpAnalyzerAsync(string source, string bannedApiText, params DiagnosticResult[] expected)
        {
            var test = new VerifyCS.Test
            {
                TestState =
                {
                    Sources = { source },
                    AdditionalFiles = { (IAsyncTaskBannedApiFileName, bannedApiText) },
                },
            };

            test.ExpectedDiagnostics.AddRange(expected);
            await test.RunAsync();
        }

        [Fact]
        public async Task NoDiagnosticWhenNotInExecuteAsyncMethod()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        return true;
    }
    
    public void SomeOtherMethod()
    {
        // This should NOT trigger the diagnostic
        Console.WriteLine(""Hello World"");
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText);
        }

        [Fact]
        public async Task DiagnosticWhenInIAsyncTaskClass()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        // This SHOULD trigger the diagnostic
        {|#0:Console.WriteLine(""Hello World"")|};
        return true;
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ""));
        }

        [Fact]
        public async Task DiagnosticWithMessage()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        {|#0:Console.WriteLine(""Hello World"")|};
        return true;
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String);Use ILogger instead";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ": Use ILogger instead"));
        }

        [Fact]
        public async Task NoDiagnosticForNonIAsyncTaskClass()
        {
            var source = @"
using System;
using System.Threading.Tasks;

public class MyTask
{
    public async Task<bool> ExecuteAsync()
    {
        // This should NOT trigger because class doesn't implement IAsyncTask
        Console.WriteLine(""Hello World"");
        return true;
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText);
        }

        [Fact]
        public async Task NoDiagnosticForWrongMethodSignature()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        return true;
    }
    
    // Wrong return type
    public async Task ExecuteAsync(string param)
    {
        // This should NOT trigger because signature is wrong
        Console.WriteLine(""Hello World"");
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText);
        }

        [Fact]
        public async Task DiagnosticForExplicitInterfaceImplementationClass()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    async Task<bool> Microsoft.Build.Framework.IAsyncTask.ExecuteAsync()
    {
        // This SHOULD trigger the diagnostic for explicit implementation
        {|#0:Console.WriteLine(""Hello World"")|};
        return true;
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String);Use ILogger instead";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ": Use ILogger instead"));
        }

        [Fact]
        public async Task DiagnosticForAnyMethodInIAsyncTaskClass()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public class MyTask : Microsoft.Build.Framework.IAsyncTask
{
    public async Task<bool> ExecuteAsync()
    {
        {|#0:Console.WriteLine(""In ExecuteAsync"")|};
        Helper();
        return true;
    }
    
    private void Helper()
    {
        {|#1:Console.WriteLine(""In Helper"")|};
    }
    
    public void SomeOtherMethod()
    {
        {|#2:Console.WriteLine(""In SomeOtherMethod"")|};
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(1, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(2, "Console.WriteLine(string?)", ""));
        }

        [Fact]
        public async Task DiagnosticForInheritanceHierarchy()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public abstract class AsyncTask : Microsoft.Build.Framework.IAsyncTask
{
    public abstract Task<bool> ExecuteAsync();
    
    protected void BaseHelper()
    {
        {|#0:Console.WriteLine(""In BaseHelper"")|};
    }
}

public class AnalyzedTask : AsyncTask
{
    public override async Task<bool> ExecuteAsync()
    {
        {|#1:Console.WriteLine(""In AnalyzedTask ExecuteAsync"")|};
        DerivedHelper();
        BaseHelper();
        return true;
    }
    
    private void DerivedHelper()
    {
        {|#2:Console.WriteLine(""In DerivedHelper"")|};
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(1, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(2, "Console.WriteLine(string?)", ""));
        }

        [Fact]
        public async Task DiagnosticForDeepInheritanceHierarchy()
        {
            var source = @"
using System;
using System.Threading.Tasks;

namespace Microsoft.Build.Framework
{
    public interface IAsyncTask
    {
        Task<bool> ExecuteAsync();
    }
}

public abstract class BaseAsyncTask : Microsoft.Build.Framework.IAsyncTask
{
    public abstract Task<bool> ExecuteAsync();
    
    protected void BaseMethod()
    {
        {|#0:Console.WriteLine(""In Base"")|};
    }
}

public abstract class MiddleAsyncTask : BaseAsyncTask
{
    protected void MiddleMethod()
    {
        {|#1:Console.WriteLine(""In Middle"")|};
    }
}

public class ConcreteAsyncTask : MiddleAsyncTask
{
    public override async Task<bool> ExecuteAsync()
    {
        {|#2:Console.WriteLine(""In Concrete ExecuteAsync"")|};
        ConcreteMethod();
        MiddleMethod();
        BaseMethod();
        return true;
    }
    
    private void ConcreteMethod()
    {
        {|#3:Console.WriteLine(""In Concrete"")|};
    }
}";

            var bannedText = @"M:System.Console.WriteLine(System.String)";

            await VerifyCSharpAnalyzerAsync(source, bannedText,
                GetCSharpResultAt(0, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(1, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(2, "Console.WriteLine(string?)", ""),
                GetCSharpResultAt(3, "Console.WriteLine(string?)", ""));
        }
    }
}
