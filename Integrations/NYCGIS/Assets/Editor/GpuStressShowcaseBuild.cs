using System;
using UnityEditor;
using UnityEngine;

public static class GpuStressShowcaseBuild
{
    [MenuItem(
        "Tools/NYC GIS Demo/GPU/Build Visual Stress A-B Player",
        priority = 80)]
    public static void BuildInteractive()
    {
        try
        {
            Debug.Log(NYCGISFullCityWeatherPerformanceQA.BuildPlayer());
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    public static void BuildBatch()
    {
        int exitCode = 0;
        try
        {
            Debug.Log(NYCGISFullCityWeatherPerformanceQA.BuildPlayer());
        }
        catch (Exception exception)
        {
            exitCode = 1;
            Debug.LogException(exception);
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }
}
