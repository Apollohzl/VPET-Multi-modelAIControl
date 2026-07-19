using System;
using System.Reflection;
using System.IO;
public class Inspector {
  public static void Main() {
    string gameDir = @"F:\SteamLibrary\steamapps\common\VPet";
    Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "VPet-Simulator.Windows.Interface.dll"));
    foreach (Type t in asm.GetTypes()) {
      if (t.Name.Contains("IMain") || t.Name.Contains("Main") || t.Name.Contains("Plugin") || t.Name.Contains("Save") || t.Name.Contains("Chat")) {
        Console.WriteLine("=== " + t.FullName + " (" + (t.IsInterface ? "interface" : "class") + ") ===");
        foreach (PropertyInfo p in t.GetProperties()) Console.WriteLine("  P: " + p.PropertyType.Name + " " + p.Name);
        foreach (FieldInfo f in t.GetFields()) Console.WriteLine("  F: " + f.FieldType.Name + " " + f.Name);
        foreach (MethodInfo m in t.GetMethods()) {
          if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
            Console.WriteLine("  M: " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), p=>p.ParameterType.Name+" "+p.Name)) + ")");
        }
      }
    }
  }
}