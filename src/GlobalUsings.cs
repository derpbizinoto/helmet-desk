// Every path the app uses lives in HelmetDesk.Core.AppPaths (core/AppPaths.cs).
global using AppPaths = HelmetDesk.Core.AppPaths;

// Every user-facing text goes through T()/TF()/TN() (core/Localization/L.cs); XAML uses {local:Tr ...}.
global using static HelmetDesk.Core.L;
global using L = HelmetDesk.Core.L;
