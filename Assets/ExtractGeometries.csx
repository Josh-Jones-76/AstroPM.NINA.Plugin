// Run with: dotnet script ExtractGeometries.csx
// Or compile as a small console app

using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

var ninaDir = @"C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy";
var outputFile = @"C:\AICodeProjects\ASG.AstroPM\ASG.AstroPM.NINA\Assets\nina_geometries.txt";

var dlls = new[] { "NINA.dll", "NINA.WPF.Base.dll", "NINA.Sequencer.dll", "NINA.Equipment.dll", "NINA.Core.dll" };
var results = new System.Collections.Generic.List<string>();

foreach (var dll in dlls) {
    var path = Path.Combine(ninaDir, dll);
    if (!File.Exists(path)) continue;

    try {
        var asm = Assembly.LoadFrom(path);
        var resNames = asm.GetManifestResourceNames();
        foreach (var resName in resNames) {
            if (!resName.EndsWith(".baml") && !resName.EndsWith(".xaml") && !resName.EndsWith(".resources")) continue;
            results.Add($"  Resource: {resName}");
        }
    } catch (Exception ex) {
        results.Add($"  Error: {ex.Message}");
    }
}

File.WriteAllLines(outputFile, results);
Console.WriteLine($"Wrote {results.Count} lines to {outputFile}");
