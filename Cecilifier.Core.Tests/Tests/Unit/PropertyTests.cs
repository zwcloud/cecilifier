using NUnit.Framework;

namespace Cecilifier.Core.Tests.Tests.Unit;

[TestFixture]
public class PropertyTests : CecilifierUnitTestBase
{
    [Test]
    public void TestGetterOnlyInitialization_Simple()
    {
        var result = RunCecilifier("class C { public int Value { get; } public C() => Value = 42; }");
        var cecilifiedCode = result.GeneratedCode.ReadToEnd();
        Assert.That(cecilifiedCode, Contains.Substring(
            @"il_ctor_6.Append(ldarg_0_7);
			il_ctor_6.Emit(OpCodes.Ldc_I4, 42);
			il_ctor_6.Emit(OpCodes.Stfld, fld_value_4);"));
    }
    
    [Test]
    public void TestGetterOnlyInitialization_Complex()
    {
        var result = RunCecilifier("class C { public int Value { get; } public C(int n) => Value = n * 2; }");
        var cecilifiedCode = result.GeneratedCode.ReadToEnd();
        Assert.That(cecilifiedCode, Contains.Substring(
            @"il_ctor_6.Append(ldarg_0_8);
			il_ctor_6.Emit(OpCodes.Ldarg_1);
			il_ctor_6.Emit(OpCodes.Ldc_I4, 2);
			il_ctor_6.Emit(OpCodes.Mul);
			il_ctor_6.Emit(OpCodes.Stfld, fld_value_4);"));
    }
    
    [Test]
    public void TestAutoPropertyWithGetterAndSetter()
    {
        var result = RunCecilifier("class C { public int Value { get; set; } public C() => Value = 42; }");
        var cecilifiedCode = result.GeneratedCode.ReadToEnd();
        Assert.That(cecilifiedCode, Contains.Substring(
            @"il_ctor_8.Emit(OpCodes.Ldc_I4, 42);
			il_ctor_8.Emit(OpCodes.Call, l_set_5);"));
    }

    [Test]
    public void TestPropertyInitializers()
    {
        var result = RunCecilifier("class C { int Value1 { get; } = 42;  int Value2 { get; } = M(21); static int M(int v) => v * 2; }");
        var cecilifiedCode = result.GeneratedCode.ReadToEnd();
        Assert.That(cecilifiedCode, Does.Match(
            @"//int Value1 { get; } = 42;\s+" +  
			@"(il_ctor_13\.Emit\(OpCodes\.)Ldarg_0\);\s+" +  
            @"\1Ldc_I4, 42\);\s+" +  
            @"\1Stfld, fld_value1_4\);\s+" +  
            @"//int Value2 { get; } = M\(21\);\s+" +  
            @"(il_ctor_13\.Emit\(OpCodes\.)Ldarg_0\);\s+" +  
            @"\1Ldc_I4, 21\);\s+" +  
            @"\1Call, m_M_9\);\s+" +  
            @"\1Stfld, fld_value2_8\);"));
    }
    
    [Test]
    public void TestSystemIndexPropertyInitializers()
    {
        var result = RunCecilifier("using System; class C { Index Value1 { get; } = ^42; }");
        var cecilifiedCode = result.GeneratedCode.ReadToEnd();
        Assert.That(cecilifiedCode, Does.Match(
            @"//Index Value1 { get; } = \^42;\s+" + 
			@"il_ctor_6.Emit\(OpCodes.Ldarg_0\);\s+" + 
            @"il_ctor_6.Emit\(OpCodes.Ldc_I4, 42\);\s+" + 
            @"il_ctor_6.Emit\(OpCodes.Ldc_I4_1\);\s+" + 
            @"il_ctor_6.Emit\(OpCodes.Newobj,.+""System.Index"", ""\.ctor"",.+""System.Int32"", ""System.Boolean"".+\);\s+" +
            @"il_ctor_6.Emit\(OpCodes.Stfld, fld_value1_4\);"));
    }
}
