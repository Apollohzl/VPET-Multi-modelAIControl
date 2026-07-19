using System;
using System.Reflection;
using System.IO;
public class Inspector {
  public static void Main() {
    string gameDir = @"F:\SteamLibrary\steamapps\common\VPet";
    foreach (string file in new[] {"VPet-Simulator.Windows.Interface.dll","VPet-Simulator.Core.dll"}) {
      string path = Path.Combine(gameDir, file);
      Console.WriteLine("=== " + file + " ===");
      Assembly asm = Assembly.LoadFrom(path);
      foreach (Type t in asm.GetTypes()) {
        if (t.IsPublic && (t.Name.Contains("IMain") || t.Name == "MainPlugin" || t.Name == "MainWindows" || t.Name == "MainWindow" || t.Name.Contains("Plugin") || t.Name.Contains("Main") || t.Name.Contains("Chat") || t.Name.Contains("Say") || t.Name.Contains("Text")) {
          Console.WriteLine("  " + t.FullName + " [" + (t.IsInterface ? "interface" : t.IsClass ? "class" : t.IsEnum ? "enum" : "type") + "]");
          if (t.IsInterface) {
            foreach (PropertyInfo p in t.GetProperties()) Console.WriteLine("    P: " + p.PropertyType.Name + " " + p.Name);
            foreach (MethodInfo m in t.GetMethods()) {
              if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                Console.WriteLine("    M: " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(",", Array.ConvertAll(m.GetParameters(), pa=>pa.ParameterType.Name+" "+pa.Name)) + ")");
            }
          }
        }
      }
    }
    // Check our plugin DLL
    string ourDll = @"F:\SteamLibrary\steamapps\workshop\content\1920960\PetChat\plugin\VPet.Plugin.PetChat.dll";
    Console.WriteLine("=== Our plugin DLL ===");
    Assembly our = Assembly.LoadFrom(ourDll);
    foreach (Type t in our.GetTypes()) {
      if (t.IsPublic) {
        Console.WriteLine("  " + t.FullName + " [" + (t.IsInterface ? "interface" : "class") + "]");
        foreach (ConstructorInfo c in t.GetConstructors()) {
          ParameterInfo[] ps = c.GetParameters();
          Console.WriteLine("    .ctor(" + string.Join(",", Array.ConvertAll(ps, pa=>pa.ParameterType.Name+" "+pa.Name)) + ")");
        }
        foreach (MethodInfo m in t.GetMethods()) {
          if (m.IsPublic && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_")) {
            Console.WriteLine("    M: " + m.ReturnType.Name + " " + m.Name);
          }
        }
      }
    }
  }
}