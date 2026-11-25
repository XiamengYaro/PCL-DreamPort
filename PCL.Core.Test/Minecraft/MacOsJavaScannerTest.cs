using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Java.Scanner;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class MacOsJavaScannerTest
{
    [TestMethod]
    public void Scan_IncludesUserJavaVirtualMachinesBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-macos-jvm-" + Guid.NewGuid().ToString("N"));
        var java = Path.Combine(root, "temurin-25.jdk", "Contents", "Home", "bin", "java");
        Directory.CreateDirectory(Path.GetDirectoryName(java)!);
        File.WriteAllText(java, string.Empty);

        try
        {
            var results = new List<string>();
            new MacOsJavaScanner([root]).Scan(results);

            CollectionAssert.Contains(results, java);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
