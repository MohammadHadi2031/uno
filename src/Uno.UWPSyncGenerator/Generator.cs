﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Win32.SafeHandles;
using Uno.Extensions;
using Uno.Logging;

namespace Uno.UWPSyncGenerator
{
	abstract class Generator
	{
		private const string net461Define = "NET461";
		private const string AndroidDefine = "__ANDROID__";
		private const string iOSDefine = "__IOS__";
		private const string MacDefine = "__MACOS__";
		private const string NetStdReferenceDefine = "__NETSTD_REFERENCE__";
		private const string WasmDefine = "__WASM__";
		private const string SkiaDefine = "__SKIA__";

#if HAS_UNO_WINUI
		private const string BaseXamlNamespace = "Microsoft.UI.Xaml";
#else
		private const string BaseXamlNamespace = "Windows.UI.Xaml";
#endif

		private Compilation _iOSCompilation;
		private Compilation _androidCompilation;
		private Compilation _macCompilation;
		private INamedTypeSymbol _iOSBaseSymbol;
		private INamedTypeSymbol _androidBaseSymbol;
		private INamedTypeSymbol _macOSBaseSymbol;
		private Compilation _referenceCompilation;
		private Compilation _net461Compilation;

		private Compilation _netstdReferenceCompilation;
		private Compilation _wasmCompilation;
		private Compilation _skiaCompilation;

		private ISymbol _voidSymbol;
		private ISymbol _dependencyPropertySymbol;
		protected ISymbol UIElementSymbol { get; private set; }
		private static string MSBuildBasePath;

		static Generator()
		{
			RegisterAssemblyLoader();
		}

		public virtual void Build(string basePath, string baseName, string sourceAssembly)
		{
			Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
			InitializeRoslyn();

			Console.WriteLine($"Generating for {baseName} {sourceAssembly}");

			_referenceCompilation = LoadProject(@"..\..\..\Uno.UWPSyncGenerator.Reference\Uno.UWPSyncGenerator.Reference.csproj");
			_iOSCompilation = LoadProject($@"{basePath}\{baseName}.csproj", "xamarinios10");
			_androidCompilation = LoadProject($@"{basePath}\{baseName}.csproj", "MonoAndroid10.0");
			_net461Compilation = LoadProject($@"{basePath}\{baseName}.csproj", "net461");
			_macCompilation = LoadProject($@"{basePath}\{baseName}.csproj", "xamarinmac20");

			_netstdReferenceCompilation = LoadProject($@"{basePath}\{baseName}.csproj", "netstandard2.0");
			_wasmCompilation = LoadProject($@"{basePath}\{baseName}.Wasm.csproj", "netstandard2.0");
			_skiaCompilation = LoadProject($@"{basePath}\{baseName}.Skia.csproj", "netstandard2.0");

			_iOSBaseSymbol = _iOSCompilation.GetTypeByMetadataName("UIKit.UIView");
			_androidBaseSymbol = _androidCompilation.GetTypeByMetadataName("Android.Views.View");
			_macOSBaseSymbol = _macCompilation.GetTypeByMetadataName("AppKit.NSView");

			_voidSymbol = _referenceCompilation.GetTypeByMetadataName("System.Void");
			_dependencyPropertySymbol = _referenceCompilation.GetTypeByMetadataName(BaseXamlNamespace + ".DependencyProperty");
			UIElementSymbol = _referenceCompilation.GetTypeByMetadataName(BaseXamlNamespace + ".UIElement");
			var a = _referenceCompilation.GetTypeByMetadataName("Microsoft.UI.ViewManagement.StatusBar");


			var origins = from externalRedfs in _referenceCompilation.ExternalReferences
						  where Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Windows.Foundation")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Microsoft.UI")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Microsoft.System")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Microsoft.ApplicationModel.Resources")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Microsoft.Graphics")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Windows.Phone.PhoneContract")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Windows.Networking.Connectivity.WwanContract")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Windows.ApplicationModel.Calls.CallsPhoneContract")
						  || Path.GetFileNameWithoutExtension(externalRedfs.Display).StartsWith("Microsoft.Web.WebView2.Core")
						  let asm = _referenceCompilation.GetAssemblyOrModuleSymbol(externalRedfs) as IAssemblySymbol
						  where asm != null
						  select asm;

			origins = origins.ToArray();

			var unoUINamespaces = new[] {
				"Windows.UI.Xaml",
#if HAS_UNO_WINUI
				"Microsoft.UI.Xaml",
				"Microsoft.UI.Composition",
				"Microsoft.UI.Text",
				"Microsoft.UI.Input",
				"Microsoft.System",
				"Microsoft.Graphics",
				"Microsoft.ApplicationModel.Resources",
				"Microsoft.Web",
#endif
			};

			var q = from asm in origins
					where asm.Name == sourceAssembly
					from targetType in GetNamespaceTypes(asm.Modules.First().GlobalNamespace)
					where targetType.DeclaredAccessibility == Accessibility.Public
					where ((baseName == "Uno" || baseName == "Uno.Foundation") && !targetType.ContainingNamespace.ToString().StartsWith("Windows.UI.Xaml") && !targetType.ContainingNamespace.ToString().StartsWith("Microsoft.UI.Xaml"))
					|| (baseName == "Uno.UI" && unoUINamespaces.Any(n => targetType.ContainingNamespace.ToString().StartsWith(n)))
					group targetType by targetType.ContainingNamespace into namespaces
					orderby namespaces.Key.MetadataName
					select new
					{
						Namespace = namespaces.Key,
						Types = namespaces
							.Where(t => t.DeclaredAccessibility == Accessibility.Public)
					};

			foreach (var ns in q)
			{
				foreach (var type in ns.Types)
				{
					ProcessType(type, ns.Namespace);
				}
			}
		}

		protected abstract void ProcessType(INamedTypeSymbol type, INamespaceSymbol ns);

		private static void InitializeRoslyn()
		{
			var installPath = Environment.GetEnvironmentVariable("VSINSTALLDIR");

			if (string.IsNullOrEmpty(installPath))
			{
				var pi = new System.Diagnostics.ProcessStartInfo(
					"cmd.exe",
					@"/c ""C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"" -property installationPath"
				)
				{
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				var process = System.Diagnostics.Process.Start(pi);
				process.WaitForExit();
				installPath = process.StandardOutput.ReadToEnd().Split('\r').First();
			}

			SetupMSBuildLookupPath(installPath);
		}

		private static void SetupMSBuildLookupPath(string installPath)
		{
			Environment.SetEnvironmentVariable("VSINSTALLDIR", installPath);

			bool MSBuildExists() => File.Exists(Path.Combine(MSBuildBasePath, "Microsoft.Build.dll"));

			MSBuildBasePath = Path.Combine(installPath, "MSBuild\\15.0\\Bin");

			if (!MSBuildExists())
			{
				MSBuildBasePath = Path.Combine(installPath, "MSBuild\\Current\\Bin");
				if (!MSBuildExists())
				{
					throw new InvalidOperationException($"Invalid Visual studio installation (Cannot find Microsoft.Build.dll)");
				}
			}
		}

		protected string GetNamespaceBasePath(INamedTypeSymbol type)
		{
			if (type.ContainingAssembly.Name == "Windows.Foundation.FoundationContract")
			{
				return @"..\..\..\Uno.Foundation\Generated\2.0.0.0";
			}
			else if (!(
				type.ContainingNamespace.ToString().StartsWith("Windows.UI.Xaml")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.UI.Xaml")
#if HAS_UNO_WINUI
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.System")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.UI.Composition")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.UI.Text")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.UI.Input")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.Graphics")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.ApplicationModel.Resources")
				|| type.ContainingNamespace.ToString().StartsWith("Microsoft.Web")
#endif
			))
			{
				return @"..\..\..\Uno.UWP\Generated\3.0.0.0";
			}
			else
			{
				return @"..\..\..\Uno.UI\Generated\3.0.0.0";
			}
		}

		protected class PlatformSymbols<T> where T : ISymbol
		{
			public T AndroidSymbol;
			public T IOSSymbol;
			public T net461ymbol;
			public T MacOSSymbol;
			public T UAPSymbol;
			public T NetStdReferenceSymbol;
			public T WasmSymbol;
			public T SkiaSymbol;

			private ImplementedFor _implementedFor;
			public ImplementedFor ImplementedFor => _implementedFor;
			public ImplementedFor ImplementedForMain => ImplementedFor & ImplementedFor.Main;

			public PlatformSymbols(
				T androidType,
				T iOSType,
				T macOSType,
				T unitTestType,
				T netStdRerefenceType,
				T wasmType,
				T skiaType,
				T uapType
			)
			{
				this.AndroidSymbol = androidType;
				this.IOSSymbol = iOSType;
				this.net461ymbol = unitTestType;
				this.MacOSSymbol = macOSType;
				this.UAPSymbol = uapType;
				this.NetStdReferenceSymbol = netStdRerefenceType;
				this.WasmSymbol = wasmType;
				this.SkiaSymbol = skiaType;

				if (IsImplemented(AndroidSymbol))
				{
					_implementedFor |= ImplementedFor.Android;
				}
				if (IsImplemented(IOSSymbol))
				{
					_implementedFor |= ImplementedFor.iOS;
				}
				if (IsImplemented(net461ymbol))
				{
					_implementedFor |= ImplementedFor.Net461;
				}
				if (IsImplemented(MacOSSymbol))
				{
					_implementedFor |= ImplementedFor.MacOS;
				}
				if (IsImplemented(NetStdReferenceSymbol))
				{
					_implementedFor |= ImplementedFor.NetStdReference;
				}
				if (IsImplemented(WasmSymbol))
				{
					_implementedFor |= ImplementedFor.WASM;
				}
				if (IsImplemented(SkiaSymbol))
				{
					_implementedFor |= ImplementedFor.Skia;
				}
			}

			public bool HasUndefined =>
				AndroidSymbol == null
				|| IOSSymbol == null
				|| net461ymbol == null
				|| MacOSSymbol == null
				|| NetStdReferenceSymbol == null
				|| WasmSymbol == null
				|| SkiaSymbol == null
				;

			public void AppendIf(IndentedStringBuilder b)
			{
				var defines = new[] {
					IsNotDefinedByUno(AndroidSymbol) ? AndroidDefine : "false",
					IsNotDefinedByUno(IOSSymbol) ? iOSDefine : "false",
					IsNotDefinedByUno(net461ymbol) ? net461Define : "false",
					IsNotDefinedByUno(WasmSymbol) ? WasmDefine : "false",
					IsNotDefinedByUno(SkiaSymbol) ? SkiaDefine : "false",
					IsNotDefinedByUno(NetStdReferenceSymbol) ? NetStdReferenceDefine : "false",
					MacOSSymbol == null ? MacDefine : "false",
				};

				b.AppendLineInvariant($"#if {defines.JoinBy(" || ")}");
			}

			public string GenerateNotImplementedList()
			{
				var defines = new[] {
					IsNotDefinedByUno(AndroidSymbol) ? AndroidDefine : "",
					IsNotDefinedByUno(IOSSymbol) ? iOSDefine : "",
					IsNotDefinedByUno(net461ymbol) ? net461Define : "",
					IsNotDefinedByUno(WasmSymbol) ? WasmDefine : "",
					IsNotDefinedByUno(SkiaSymbol) ? SkiaDefine : "",
					IsNotDefinedByUno(NetStdReferenceSymbol) ? NetStdReferenceDefine : "",
					MacOSSymbol == null ? MacDefine : "",
				};

				return defines.Where(d => !string.IsNullOrEmpty(d)).Select(d => $"\"{d}\"").JoinBy(", ");
			}

			private static bool IsNotDefinedByUno(ISymbol symbol)
			{
				if (symbol == null) { return true; }
				if (!(symbol is INamedTypeSymbol type)) { return false; }

				var onlyGenerated = type.DeclaringSyntaxReferences.All(r => IsGeneratedFile(r.SyntaxTree.FilePath));
				return onlyGenerated;
			}

			private static bool IsGeneratedFile(string filePath)
			{
				if (filePath.EndsWith(".g.cs"))
				{
					return true;
				}
				if (filePath.Contains(@"Generated\3.0.0.0"))
				{
					return true;
				}
				if (filePath.Contains(@"Generated\2.0.0.0"))
				{
					return true;
				}

				return false;
			}

			private static bool IsImplemented(ISymbol symbol)
			{
				if (symbol == null) { return false; }
				if (symbol.GetAttributes().Any(a => a.AttributeClass.Name == "NotImplementedAttribute")) { return false; }
				if (IsNotDefinedByUno(symbol)) { return false; }

				return true;
			}
		}

		protected PlatformSymbols<INamedTypeSymbol> GetAllSymbols(INamedTypeSymbol uapType)
		{
			var name = uapType.ContainingNamespace + "." + uapType.MetadataName;
			return new PlatformSymbols<INamedTypeSymbol>(
				  androidType: _androidCompilation.GetTypeByMetadataName(name),
				  iOSType: _iOSCompilation.GetTypeByMetadataName(name),
				  macOSType: _macCompilation?.GetTypeByMetadataName(name),
				  unitTestType: _net461Compilation.GetTypeByMetadataName(name),
				  netStdRerefenceType: _netstdReferenceCompilation.GetTypeByMetadataName(name),
				  wasmType: _wasmCompilation.GetTypeByMetadataName(name),
				  skiaType: _skiaCompilation.GetTypeByMetadataName(name),
				  uapType: uapType
			  );
		}

		private PlatformSymbols<ISymbol> GetAllGetNonGeneratedMembers(PlatformSymbols<INamedTypeSymbol> types, string name, Func<IEnumerable<ISymbol>, ISymbol> filter, ISymbol uapSymbol = null)
		{
			var android = GetNonGeneratedMembers(types.AndroidSymbol, name);
			var ios = GetNonGeneratedMembers(types.IOSSymbol, name);
			var macOS = GetNonGeneratedMembers(types.MacOSSymbol, name);
			var net461 = GetNonGeneratedMembers(types.net461ymbol, name);
			var netStdReference = GetNonGeneratedMembers(types.NetStdReferenceSymbol, name);
			var wasm = GetNonGeneratedMembers(types.WasmSymbol, name);
			var skia = GetNonGeneratedMembers(types.SkiaSymbol, name);

			return new PlatformSymbols<ISymbol>(
				androidType: filter(android),
				iOSType: filter(ios),
				macOSType: filter(macOS),
				unitTestType: filter(net461),
				netStdRerefenceType: filter(netStdReference),
				wasmType: filter(wasm),
				skiaType: filter(skia),
				uapType: uapSymbol
			);
		}

		protected PlatformSymbols<IMethodSymbol> GetAllMatchingMethods(PlatformSymbols<INamedTypeSymbol> types, IMethodSymbol method)
			=> new PlatformSymbols<IMethodSymbol>(
				androidType: FindMatchingMethod(types.AndroidSymbol, method),
				iOSType: FindMatchingMethod(types.IOSSymbol, method),
				macOSType: FindMatchingMethod(types.MacOSSymbol, method),
				unitTestType: FindMatchingMethod(types.net461ymbol, method),
				netStdRerefenceType: FindMatchingMethod(types.NetStdReferenceSymbol, method),
				wasmType: FindMatchingMethod(types.WasmSymbol, method),
				skiaType: FindMatchingMethod(types.SkiaSymbol, method),
				uapType: method
			);

		protected PlatformSymbols<IPropertySymbol> GetAllMatchingPropertyMember(PlatformSymbols<INamedTypeSymbol> types, IPropertySymbol property)
			=> new PlatformSymbols<IPropertySymbol>(
				androidType: GetMatchingPropertyMember(types.AndroidSymbol, property),
				iOSType: GetMatchingPropertyMember(types.IOSSymbol, property),
				macOSType: GetMatchingPropertyMember(types.MacOSSymbol, property),
				unitTestType: GetMatchingPropertyMember(types.net461ymbol, property),
				netStdRerefenceType: GetMatchingPropertyMember(types.NetStdReferenceSymbol, property),
				wasmType: GetMatchingPropertyMember(types.WasmSymbol, property),
				skiaType: GetMatchingPropertyMember(types.SkiaSymbol, property),
				uapType: property
			);

		protected PlatformSymbols<ISymbol> GetAllMatchingEvents(PlatformSymbols<INamedTypeSymbol> types, IEventSymbol eventMember)
			=> GetAllGetNonGeneratedMembers(types, eventMember.Name, q => q.OfType<IEventSymbol>().FirstOrDefault(), eventMember);

		protected bool SkippedType(INamedTypeSymbol type)
		{
			var v = type.ToString();
			switch (v)
			{
				case "Windows.Foundation.IAsyncOperation<TResult>":
					// Skipped to include generic variance.
					return true;

				case "Windows.Foundation.Uri":
				case BaseXamlNamespace + ".Input.ICommand":
				case BaseXamlNamespace + ".Controls.UIElementCollection":
					// Skipped because the reported interfaces are mismatched.
					return true;

				case BaseXamlNamespace + ".Media.FontFamily":
				case BaseXamlNamespace + ".Controls.IconElement":
				case BaseXamlNamespace + ".Data.ICollectionView":
				case BaseXamlNamespace + ".Data.CollectionView":
					// Skipped because the reported interfaces are mismatched.
					return true;

				case "Windows.UI.ViewManagement.InputPane":
				case "Windows.UI.ViewManagement.InputPaneVisibilityEventArgs":
					// Skipped because a dependency on FocusManager
					return true;

				case "Windows.ApplicationModel.Store.Preview.WebAuthenticationCoreManagerHelper":
					// Skipped because a cross layer dependency to Windows.UI.Xaml
					return true;

				case "Microsoft.UI.Xaml.Controls.XamlControlsResources":
					// Skipped because the type is placed in the Uno.UI.FluentTheme assembly
					return true;
			}


			return false;
		}

		protected void BuildInterfaceImplementations(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			if (type.TypeKind != TypeKind.Interface)
			{
				var implementedInterfaces = new HashSet<INamedTypeSymbol>();

				foreach (var iface in type.Interfaces.Where(i => i.DeclaredAccessibility == Accessibility.Public))
				{
					if (
						iface.MetadataName == "Windows.Foundation.IAsyncAction"
						|| iface.ToDisplayString() == "Windows.Foundation.IStringable"
						|| iface.OriginalDefinition.MetadataName == "Windows.Foundation.Collections.IIterator`1"
						|| iface.OriginalDefinition.MetadataName == "Windows.Foundation.IAsyncOperation`1"
					)
					{
						continue;
					}

					var enumerable = GetAllInterfaces(iface).Distinct(new NamedTypeSymbolStringComparer()).ToArray();

					foreach (var inner in enumerable)
					{
						if (!implementedInterfaces.Contains(inner))
						{
							implementedInterfaces.Add(inner);

							BuildInterfaceImplementation(b, type, inner, iface.TypeArguments, types, writtenSymbols);
						}
					}
				}
			}
		}

		private IEnumerable<INamedTypeSymbol> GetAllInterfaces(INamedTypeSymbol roslynInterface)
		{
			yield return roslynInterface;

			foreach (var iface in roslynInterface.Interfaces)
			{
				foreach (var inner in GetAllInterfaces(iface))
				{
					yield return inner;
				}
			}
		}

		private void BuildInterfaceImplementation(
			IndentedStringBuilder b,
			INamedTypeSymbol ownerType,
			INamedTypeSymbol ifaceSymbol,
			ImmutableArray<ITypeSymbol> genericParameters,
			PlatformSymbols<INamedTypeSymbol> types,
			List<ISymbol> writtenSymbols
		)
		{
			b.AppendLineInvariant($"// Processing: {ifaceSymbol}");

			foreach (var method in ifaceSymbol.GetMembers().OfType<IMethodSymbol>())
			{
				var isSpecialType = method.MethodKind == MethodKind.PropertyGet
					|| method.MethodKind == MethodKind.PropertySet
					|| method.MethodKind == MethodKind.EventAdd
					|| method.MethodKind == MethodKind.EventRemove
					|| method.MethodKind == MethodKind.EventRaise
					;

				var isDefinedInClass = ownerType.GetMembers().OfType<IMethodSymbol>().Any(m =>
						m.Name == method.Name
						&& m.DeclaredAccessibility == Accessibility.Public
						&& m.Parameters.Select(p => p.Type.ToDisplayString(NullableFlowState.None)).SequenceEqual(method.Parameters.Select(p2 => p2.Type.ToDisplayString(NullableFlowState.None)))
						&& m.ReturnType.ToDisplayString(NullableFlowState.None) == method.ReturnType.ToDisplayString(NullableFlowState.None)
					);

				var isAlreadyGenerated = writtenSymbols.OfType<IMethodSymbol>().Any(m => m.Name == method.Name
						&& m.DeclaredAccessibility == Accessibility.Public
						&& m.Parameters.Select(p => p.Type.ToDisplayString(NullableFlowState.None)).SequenceEqual(method.Parameters.Select(p2 => p2.Type.ToDisplayString(NullableFlowState.None)))
						&& m.ReturnType.ToDisplayString(NullableFlowState.None) == method.ReturnType.ToDisplayString(NullableFlowState.None)
					);

				if (
					isSpecialType
					|| isDefinedInClass
					|| !IsNotUWPMapping(ownerType, method)
					|| isAlreadyGenerated
				)
				{
					continue;
				}

				var allMethods = GetAllGetNonGeneratedMembers(types, method.Name, q => q.OfType<IMethodSymbol>().FirstOrDefault());

				if (allMethods.HasUndefined)
				{
					allMethods.AppendIf(b);
					var parms = string.Join(", ", method.Parameters.Select(p => $"{RefKindFormat(p)} {TransformType(ifaceSymbol, genericParameters, p.Type)} {SanitizeParameter(p.Name)}"));
					var returnTypeName = TransformType(ifaceSymbol, genericParameters, method.ReturnType);
					var typeAccessibility = GetMethodAccessibility(method);
					var explicitImplementation = typeAccessibility == "" ? $"global::{ifaceSymbol.ToString()}." : "";

					b.AppendLineInvariant($"// DeclaringType: {ifaceSymbol}");

					b.AppendLineInvariant($"[global::Uno.NotImplemented({allMethods.GenerateNotImplementedList()})]");
					using (b.BlockInvariant($"{typeAccessibility} {returnTypeName} {explicitImplementation}{method.Name}({parms})"))
					{
						b.AppendLineInvariant($"throw new global::System.NotSupportedException();");
					}

					b.AppendLineInvariant($"#endif");
				}
			}

			foreach (var property in ifaceSymbol.GetMembers().OfType<IPropertySymbol>())
			{
				var propertyTypeName = TransformType(ifaceSymbol, genericParameters, property.Type);
				var parms = string.Join(", ", property.GetMethod?.Parameters.Select(p => $"{TransformType(ifaceSymbol, genericParameters, p.Type)} {SanitizeParameter(p.Name)}") ?? new string[0]);

				var allProperties = GetAllMatchingPropertyMember(types, property);

				if (ownerType.GetMembers().OfType<IPropertySymbol>().Any(p =>
					   p.Name == property.Name
					   && p.Type.ToDisplayString() == property.Type.ToDisplayString()
					)
					|| !IsNotUWPMapping(ownerType, property))
				{
					return;
				}

				if (allProperties.HasUndefined)
				{
					allProperties.AppendIf(b);

					var v = property.IsIndexer ? $"public {propertyTypeName} this[{parms}]" : $"public {propertyTypeName} {property.Name}";

					b.AppendLineInvariant($"[global::Uno.NotImplemented({allProperties.GenerateNotImplementedList()})]");
					using (b.BlockInvariant(v))
					{
						if (property.GetMethod != null)
						{
							using (b.BlockInvariant($"get"))
							{
								b.AppendLineInvariant($"throw new global::System.NotSupportedException();");
							}
						}
						if (property.GetMethod != null)
						{
							using (b.BlockInvariant($"set"))
							{
								b.AppendLineInvariant($"throw new global::System.NotSupportedException();");
							}
						}
					}

					b.AppendLineInvariant($"#endif");
				}
				else
				{
					b.AppendLineInvariant($"// Skipping already implement {property}");
				}
			}
		}

		private IPropertySymbol GetMatchingPropertyMember(INamedTypeSymbol androidType, IPropertySymbol property)
		{
			return GetNonGeneratedMembers(androidType, property.Name)
								.OfType<IPropertySymbol>()
								.Where(prop => prop.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(property.Parameters.Select(p => p.Type.ToDisplayString())) && prop.Type.ToDisplayString() == property.Type.ToDisplayString())
								.FirstOrDefault();
		}

		private static string RefKindFormat(IParameterSymbol p)
		{
			return (p.RefKind != RefKind.None ? p.RefKind.ToString() : "").ToLowerInvariant();
		}

		private string GetMethodAccessibility(IMethodSymbol method)
		{
			if (
				method.ContainingType.ToString() == "System.Collections.IEnumerable"
				&& method.Name == "GetEnumerator"
			)
			{
				return "";
			}
			else
			{
				return "public";
			}
		}

		private string TransformType(INamedTypeSymbol ifaceSymbol, ImmutableArray<ITypeSymbol> genericParameters, ITypeSymbol typeSymbol)
		{
			var originalTypeSymbol = typeSymbol;
			var namedType = typeSymbol as INamedTypeSymbol;

			if (namedType != null)
			{
				return namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			}

			if (typeSymbol is IArrayTypeSymbol)
			{
				typeSymbol = (typeSymbol as IArrayTypeSymbol).ElementType;
			}

			return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + (originalTypeSymbol is IArrayTypeSymbol ? "[]" : "");
		}

		private string MapGenericParameters(ImmutableArray<ITypeSymbol> genericParameters, ITypeSymbol typeSymbol)
		{
			var typeName = typeSymbol.ToString();

			foreach (var typeRef in genericParameters.Select((param, index) => new { param, index }))
			{
				typeName = typeName.Replace($"__helper{typeRef.index}__", SanitizeType(typeRef.param));
			}

			if (typeName.StartsWith("System."))
			{
				typeName = "global::" + typeName;
			}

			return typeName;
		}

		protected string BuildInterfaces(INamedTypeSymbol type)
		{
			var ifaces = new List<string>();

			if (HasValidBaseType(type))
			{
				ifaces.Add($"{SanitizeType(type.BaseType)}");
			}

			foreach (var iface in type.Interfaces)
			{
				if (iface.DeclaredAccessibility == Accessibility.Public
					&& iface.MetadataName != "Windows.Foundation.IStringable")
				{
					ifaces.Add(MapUWPTypes(SanitizeType(iface)));
				}
			}

			if (ifaces.Any())
			{
				return $": {string.Join(",", ifaces)}";
			}

			return "";
		}

		private static bool HasValidBaseType(INamedTypeSymbol type)
		{
			string[] skippedTypes = new[] {
				"object",
				"System.Enum",
				"System.ValueType",
			};

			string[] skipBaseTypes = new[] {

				// skipped because of legacy mismatched hierarchy
				BaseXamlNamespace + ".FrameworkElement",
				BaseXamlNamespace + ".UIElement",
				BaseXamlNamespace + ".Controls.Image",
				BaseXamlNamespace + ".Controls.CalendarViewDayItem",
				BaseXamlNamespace + ".Controls.ComboBox",
				BaseXamlNamespace + ".Controls.CheckBox",
				BaseXamlNamespace + ".Controls.TextBlock",
				BaseXamlNamespace + ".Controls.TextBox",
				BaseXamlNamespace + ".Controls.ProgressRing",
				BaseXamlNamespace + ".Controls.ListViewBase",
				BaseXamlNamespace + ".Controls.ListView",
				BaseXamlNamespace + ".Controls.ListViewHeaderItem",
				BaseXamlNamespace + ".Controls.GridView",
				BaseXamlNamespace + ".Controls.ComboBox",
				BaseXamlNamespace + ".Controls.UserControl",
				BaseXamlNamespace + ".Controls.RadioButton",
				BaseXamlNamespace + ".Controls.Slider",
				BaseXamlNamespace + ".Controls.PasswordBox",
				BaseXamlNamespace + ".Controls.RichEditBox",
				BaseXamlNamespace + ".Controls.ProgressBar",
				BaseXamlNamespace + ".Controls.ListViewItem",
				BaseXamlNamespace + ".Controls.ScrollContentPresenter",
				BaseXamlNamespace + ".Controls.Pivot",
				BaseXamlNamespace + ".Controls.CommandBar",
				BaseXamlNamespace + ".Controls.AppBar",
				BaseXamlNamespace + ".Controls.TimePickerFlyoutPresenter",
				BaseXamlNamespace + ".Controls.DatePickerFlyoutPresenter",
				BaseXamlNamespace + ".Controls.AppBarSeparator",
				BaseXamlNamespace + ".Controls.DatePickerFlyout",
				BaseXamlNamespace + ".Controls.TimePickerFlyout",
				BaseXamlNamespace + ".Controls.AppBarToggleButton",
				BaseXamlNamespace + ".Controls.FlipView",
				BaseXamlNamespace + ".Controls.FlipViewItem",
				BaseXamlNamespace + ".Controls.GridViewItem",
				BaseXamlNamespace + ".Controls.ComboBoxItem",
				BaseXamlNamespace + ".Controls.Flyout",
				BaseXamlNamespace + ".Controls.FontIcon",
				BaseXamlNamespace + ".Controls.MenuFlyout",
				BaseXamlNamespace + ".Data.CollectionView",
				BaseXamlNamespace + ".Controls.WebView",
				BaseXamlNamespace + ".Controls.UIElementCollection",
				BaseXamlNamespace + ".Shapes.Polygon",
				BaseXamlNamespace + ".Shapes.Polyline",
				BaseXamlNamespace + ".Shapes.Ellipse",
				BaseXamlNamespace + ".Shapes.Line",
				BaseXamlNamespace + ".Shapes.Path",
				BaseXamlNamespace + ".Media.Animation.FadeInThemeAnimation",
				BaseXamlNamespace + ".Media.Animation.FadeOutThemeAnimation",
				BaseXamlNamespace + ".Media.ImageBrush",
				BaseXamlNamespace + ".Media.LinearGradientBrush",
				BaseXamlNamespace + ".Media.RadialGradientBrush",
				BaseXamlNamespace + ".Data.RelativeSource",
				BaseXamlNamespace + ".Controls.Primitives.CarouselPanel",
				BaseXamlNamespace + ".Controls.MediaPlayerPresenter",
				BaseXamlNamespace + ".Controls.NavigationViewItemBase",

#if HAS_UNO_WINUI
				// Mismatching public inheritance hierarchy because RadioMenuFlyoutItem has double inheritance in WinUI.
				// Remove this and update RadioMenuFlyoutItem if WinUI 3 removed the double inheritance.
				BaseXamlNamespace + ".Controls.RadioMenuFlyoutItem"
#endif
			};

			var isSkipped = skippedTypes.Contains(type.BaseType?.ToString());
			var isBaseSkipped = skipBaseTypes.Contains(type.ToString());

			// Console.WriteLine($"Checking {type.MetadataName}: isSkipped {isSkipped}, isBaseSkipped {isBaseSkipped} ");

			if (type.BaseType != null && !isSkipped && !isBaseSkipped)
			{
				return true;
			}

			return false;
		}

		protected void BuildDelegate(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			if (types.HasUndefined)
			{
				types.AppendIf(b);

				var IMethodSymbol = type.GetMembers().OfType<IMethodSymbol>().First(m => m.Name == "Invoke");
				var members = string.Join(", ", IMethodSymbol.Parameters.Select(p => $"{SanitizeType(p.Type)} {SanitizeParameter(p.Name)}"));

				b.AppendLineInvariant($"public delegate {SanitizeType(IMethodSymbol.ReturnType)} {type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}({members});");

				b.AppendLineInvariant($"#endif");
			}
			else
			{
				b.AppendLineInvariant($"// Skipping already declared delegate {type}");
			}
		}

		protected void BuildFields(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
			{
				var allmembers = GetAllGetNonGeneratedMembers(types, field.Name, q => q.Where(m => m is IFieldSymbol || m is IPropertySymbol).FirstOrDefault());

				if (allmembers.HasUndefined)
				{
					allmembers.AppendIf(b);

					var staticQualifier = field.IsStatic ? "static" : "";

					if (type.TypeKind == TypeKind.Enum)
					{
						// if (!field.IsSpecialName)
						{
							b.AppendLineInvariant($"{field.Name},");
						}
					}
					else
					{
						b.AppendLineInvariant($"public {staticQualifier} {SanitizeType(field.Type)} {field.Name};");
					}

					b.AppendLineInvariant($"#endif");
				}
				else
				{
					b.AppendLineInvariant($"// Skipping already declared field {field}");
				}
			}
		}

		protected void BuildEvents(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			foreach (var eventMember in type.GetMembers().OfType<IEventSymbol>())
			{
				if (!IsNotUWPMapping(type, eventMember) || SkipEvent(type, eventMember))
				{
					continue;
				}

				var allMembers = GetAllMatchingEvents(types, eventMember);

				if (allMembers.HasUndefined)
				{
					allMembers.AppendIf(b);

					var staticQualifier = eventMember.AddMethod.IsStatic ? "static" : "";
					var declaration = $"{staticQualifier} event {MapUWPTypes(SanitizeType(eventMember.Type))} {eventMember.Name}";

					if (type.TypeKind == TypeKind.Interface)
					{
						b.AppendLineInvariant($"{declaration};");
					}
					else
					{
						b.AppendLineInvariant($"[global::Uno.NotImplemented({allMembers.GenerateNotImplementedList()})]");
						using (b.BlockInvariant($"public {declaration}"))
						{
							b.AppendLineInvariant($"[global::Uno.NotImplemented({allMembers.GenerateNotImplementedList()})]");
							using (b.BlockInvariant($"add"))
							{
								BuildNotImplementedException(b, eventMember, false);
							}
							b.AppendLineInvariant($"[global::Uno.NotImplemented({allMembers.GenerateNotImplementedList()})]");
							using (b.BlockInvariant($"remove"))
							{
								BuildNotImplementedException(b, eventMember, false);
							}
						}
					}

					b.AppendLineInvariant($"#endif");
				}
				else
				{
					b.AppendLineInvariant($"// Skipping already declared event {eventMember}");
				}
			}
		}

		private void BuildNotImplementedException(IndentedStringBuilder b, ISymbol member, bool forceRaise)
		{
			var typeName = member.ContainingType.ToDisplayString();
			var memberName = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

			if (forceRaise)
			{
				b.AppendLineInvariant(
					$"throw new global::System.NotImplementedException(\"The member {memberName} is not implemented in Uno.\");"
				);
			}
			else
			{
				b.AppendLineInvariant(
					$"global::Windows.Foundation.Metadata.ApiInformation.TryRaiseNotImplemented(\"{typeName}\", \"{memberName}\");"
				);
			}
		}

		protected void BuildMethods(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
			{
				var methods = GetAllMatchingMethods(types, method);

				var parameters = string.Join(", ", method.Parameters.Select(p => $"{GetParameterRefKind(p)} {SanitizeType(p.Type)} {SanitizeParameter(p.Name)}"));
				var staticQualifier = method.IsStatic ? "static" : "";
				var overrideQualifier = method.Name == "ToString" ? "override" : "";
				var virtualQualifier = method.IsVirtual ? "virtual" : "";
				var visiblity = method.DeclaredAccessibility.ToString().ToLowerInvariant();

				if (IsObjectCtor(methods.AndroidSymbol))
				{
					methods.AndroidSymbol = null;
				}

				if (IsObjectCtor(methods.IOSSymbol))
				{
					methods.IOSSymbol = null;
				}

				if (
					method.MethodKind == MethodKind.Constructor
					&& type.TypeKind != TypeKind.Interface
					&& type.Name != "DependencyObject"
					&& (
						!type.IsValueType
						|| (type.IsValueType && method.Parameters.Length != 0)
					)
				)
				{
					if (methods.HasUndefined)
					{
						methods.AppendIf(b);

						var q = from ctor in type.BaseType?.GetMembers().OfType<IMethodSymbol>()
								where ctor.MethodKind == MethodKind.Constructor
								where ctor.Parameters.Length == 0 // If none, match it we don't care if it's actually called.
								|| ctor
									.Parameters
									.Select(p => p.Type)
									.SequenceEqual(
										method
											.Parameters
											.Take(ctor.Parameters.Length)
											.Select(p => p.Type)
											, new InheritanceTypeComparer()
									)
								select ctor;

						var baseParamString = string.Join(", ", q.FirstOrDefault()?.Parameters.Select(p => p.Name) ?? new string[0]);

						var baseParams = type.BaseType?.Name != "Object" && q.Any() ? $": base({baseParamString})" : "";

						b.AppendLineInvariant($"[global::Uno.NotImplemented({methods.GenerateNotImplementedList()})]");
						using (b.BlockInvariant($"{visiblity} {type.Name}({parameters}) {baseParams}"))
						{
							BuildNotImplementedException(b, method, false);
						}

						b.AppendLineInvariant($"#endif");
						writtenSymbols.Add(method);
					}
					else
					{
						b.AppendLineInvariant($"// Skipping already declared method {method}");
					}
				}

				if (
						method.MethodKind == MethodKind.Ordinary
						&& !SkipMethod(type, method)
						&& IsNotUWPMapping(type, method)
						&& (
							method.DeclaredAccessibility == Accessibility.Public
							|| method.DeclaredAccessibility == Accessibility.Protected
						)
					)
				{
					if (methods.HasUndefined)
					{
						methods.AppendIf(b);

						var declaration = $"{SanitizeType(method.ReturnType)} {method.Name}({parameters})";

						if (type.TypeKind == TypeKind.Interface || type.Name == "DependencyObject")
						{
							b.AppendLineInvariant($"{declaration};");
						}
						else
						{
							b.AppendLineInvariant($"[global::Uno.NotImplemented({methods.GenerateNotImplementedList()})]");
							using (b.BlockInvariant($"{visiblity} {staticQualifier}{overrideQualifier}{virtualQualifier} {declaration}"))
							{
								var filteredName = method.Name.TrimStart("Get", StringComparison.Ordinal).TrimStart("Set", StringComparison.Ordinal);
								var isAttachedPropertyMethod =
									(method.Name.StartsWith("Get") || method.Name.StartsWith("Set"))
									&& method.IsStatic
									&& type
										.GetMembers(filteredName + "Property")
										.OfType<IPropertySymbol>()
										.Where(f => SymbolEqualityComparer.Default.Equals(f.Type, _dependencyPropertySymbol))
										.Any();

								if (isAttachedPropertyMethod)
								{
									var instanceParamName = SanitizeParameter(method.Parameters.First().Name);

									if (method.Name.StartsWith("Get"))
									{
										b.AppendLineInvariant($"return ({SanitizeType(method.ReturnType)}){instanceParamName}.GetValue({filteredName}Property);");

									}
									else if (method.Name.StartsWith("Set"))
									{
										var valueParamName = SanitizeParameter(method.Parameters.ElementAt(1).Name);
										b.AppendLineInvariant($"{instanceParamName}.SetValue({filteredName}Property, {valueParamName});");
									}
								}
								else
								{
									bool hasReturnValue =
										!SymbolEqualityComparer.Default.Equals(method.ReturnType, _voidSymbol)
										|| method.Parameters.Any(p => p.RefKind == RefKind.Out);

									BuildNotImplementedException(b, method, hasReturnValue);
								}
							}
						}

						b.AppendLineInvariant($"#endif");

						writtenSymbols.Add(method);
					}
					else
					{
						b.AppendLineInvariant($"// Skipping already declared method {method}");
					}
				}
				else
				{
					b.AppendLineInvariant($"// Forced skipping of method {method}");
				}
			}
		}

		private bool SkipMethod(INamedTypeSymbol type, IMethodSymbol method)
		{
			if (method.ContainingType.Name == "Grid")
			{
				switch (method.Name)
				{
					// The base type does not match for this parameter until Uno adjusts the
					// hierarchy based on IFrameworkElement.
					case "SetRow":
					case "SetRowSpan":
					case "SetColumn":
					case "SetColumnSpan":
					case "GetRow":
					case "GetRowSpan":
					case "GetColumn":
					case "GetColumnSpan":
						return true;
				}
			}

			if (method.ContainingType.Name == "FrameworkElement")
			{
				switch (method.Name)
				{
					// Those two members are located in DependencyObject but will need to be
					// moved up.
					case "GetBindingExpression":
					case "SetBinding":
						return true;
				}
			}

			return false;
		}

		private bool SkipEvent(INamedTypeSymbol type, IEventSymbol eventMember)
		{
			if (eventMember.ContainingType.Name == "FrameworkElement")
			{
				switch (eventMember.Name)
				{
					// Those two members are located in DependencyObject but will need to be
					// moved up.
					case "DataContextChanged":
						return true;
				}
			}
			return false;
		}

		private bool IsObjectCtor(IMethodSymbol androidMember)
			=> androidMember?.Name == ".ctor" && androidMember?.OriginalDefinition.ContainingType.Name == "Object";

		private static string GetParameterRefKind(IParameterSymbol p)
			=> p.RefKind != RefKind.None ? p.RefKind.ToString().ToLowerInvariant() : "";

		private bool IsNotUWPMapping(INamedTypeSymbol type, IEventSymbol eventMember)
		{
			foreach (var iface in type.Interfaces.SelectMany(GetAllInterfaces))
			{
				var uwpIface = GetUWPIFace(iface);

				if (uwpIface != null)
				{
					if (
							uwpIface == BaseXamlNamespace + ".Input.ICommand"
							&& eventMember.Name == "CanExecuteChanged"
						)
					{
						return false;
					}
				}
			}

			return true;
		}

		private bool IsNotUWPMapping(INamedTypeSymbol type, IMethodSymbol method)
		{
			foreach (var iface in type.Interfaces.SelectMany(GetAllInterfaces))
			{
				var uwpIface = GetUWPIFace(iface);

				if (uwpIface != null)
				{
					if (
						(
							uwpIface == "Windows.Foundation.Collections.IMap`2"
							&& (
								method.Name == "Clear"
								|| (method.Name == "Remove" && method.ReturnType.Name == "Boolean")
							)
						)
						||
						(
							uwpIface == "Windows.Foundation.Collections.IVector`1"
							&& method.Name == "Clear"
						)
					)
					{
						return true;
					}
					else if (
							uwpIface == "Windows.Foundation.Collections.IVectorView`1"
							&& method.Name == "Item"
						)
					{
						return false;
					}
					else
					{
						var type2 = _referenceCompilation.GetTypeByMetadataName(uwpIface);

						INamedTypeSymbol build()
						{
							if (iface.TypeArguments.Length != 0)
							{
								return type2.Construct(iface.TypeArguments.ToArray());
							}

							return type2;
						}

						var t3 = build();

						var q = from sourceMethod in t3.GetMembers().OfType<IMethodSymbol>()
								where sourceMethod.Name == method.Name
								&& sourceMethod.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(method.Parameters.Select(p => p.Type.ToDisplayString()))
								select sourceMethod;

						if (q.Any())
						{
							return false;
						}
					}
				}
			}

			return true;
		}

		private bool IsNotUWPMapping(INamedTypeSymbol type, IPropertySymbol property)
		{
			foreach (var iface in type.Interfaces.SelectMany(GetAllInterfaces))
			{
				var uwpIface = GetUWPIFace(iface);

				if (uwpIface != null)
				{
					var type2 = _referenceCompilation.GetTypeByMetadataName(uwpIface);

					var t3 = type2.Construct(iface.TypeArguments.ToArray());

					var q = from sourceProperty in t3.GetMembers().OfType<IPropertySymbol>()
							where sourceProperty.Name == property.Name && SymbolEqualityComparer.Default.Equals(sourceProperty.Type, property.Type)
							select sourceProperty;

					if (q.Any())
					{
						return false;
					}
				}
			}

			return true;
		}

		private string GetUWPIFace(INamedTypeSymbol iface)
		{
			switch (iface.ConstructedFrom.ToDisplayString())
			{
				case "System.Collections.Generic.IEnumerable<T>":
					return "Windows.Foundation.Collections.IIterable`1";
				case "System.Threading.Tasks.Task":
					return "Windows.Foundation.IAsyncOperation";
				case "System.Collections.Generic.IReadOnlyDictionary":
					return "Windows.Foundation.Collections.IMapView";
				case "System.Collections.Generic.IDictionary<TKey, TValue>":
					return "Windows.Foundation.Collections.IMap`2";
				case "System.Nullable":
					return "Windows.Foundation.IReference";
				case "System.Collections.Generic.IReadOnlyList<T>":
					return "Windows.Foundation.Collections.IVectorView`1";
				case "System.Collections.Generic.IList<T>":
					return "Windows.Foundation.Collections.IVector`1";
				case "System.DateTimeOffset":
					return "Windows.Foundation.DateTime";
				case "System.EventHandler":
					return "Windows.Foundation.EventHandler";
				case "System.TimeSpan":
					return "Windows.Foundation.TimeSpan";
				case "System.Collections.Generic.KeyValuePair":
					return "Windows.Foundation.Collections.IKeyValuePair";
				case "System.Collections.Specialized.INotifyCollectionChanged":
					return "Windows.UI.Xaml.Interop.INotifyCollectionChanged";
				case "System.Type":
					return BaseXamlNamespace + ".Interop.TypeName";
				case "System.Uri":
					return "Windows.Foundation.Uri";
				case "System.Windows.Input.ICommand":
					return BaseXamlNamespace + ".Input.ICommand";
			}

			return null;
		}



		protected void BuildProperties(INamedTypeSymbol type, IndentedStringBuilder b, PlatformSymbols<INamedTypeSymbol> types, List<ISymbol> writtenSymbols)
		{
			foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
			{
				var allMembers = GetAllGetNonGeneratedMembers(types, property.Name, q => q?.Where(m => m is IPropertySymbol || m is IFieldSymbol).FirstOrDefault());

				var staticQualifier = ((property.GetMethod?.IsStatic ?? false) || (property.SetMethod?.IsStatic ?? false)) ? "static" : "";

				if (SkipProperty(property))
				{
					continue;
				}

				if (allMembers.HasUndefined)
				{
					allMembers.AppendIf(b);

					if (type.TypeKind == TypeKind.Interface)
					{
						using (b.BlockInvariant($"{MapUWPTypes(SanitizeType(property.Type))} {property.Name}"))
						{
							if (property.GetMethod != null)
							{
								b.AppendLineInvariant($"get;");
							}

							if (property.SetMethod != null)
							{
								b.AppendLineInvariant($"set;");
							}
						}
					}
					else
					{
						b.AppendLineInvariant($"[global::Uno.NotImplemented({allMembers.GenerateNotImplementedList()})]");

						bool isDependencyPropertyDeclaration = property.IsStatic
							&& property.Name.EndsWith("Property")
							&& SymbolEqualityComparer.Default.Equals(property.Type, _dependencyPropertySymbol);

						if (isDependencyPropertyDeclaration)
						{
							var propertyName = property.Name.Substring(0, property.Name.LastIndexOf("Property"));

							var getAttached = property.ContainingType.GetMembers("Get" + propertyName).OfType<IMethodSymbol>().FirstOrDefault();
							var getLocal = property.ContainingType.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();

							if (getLocal != null || getAttached != null)
							{
								var attachedModifier = getAttached != null ? "Attached" : "";
								var propertyDisplayType = MapUWPTypes((getAttached?.ReturnType ?? getLocal?.Type).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

								b.AppendLineInvariant($"public {staticQualifier} {SanitizeType(property.Type)} {property.Name} {{{{ get; }}}} = ");

								b.AppendLineInvariant($"{BaseXamlNamespace}.DependencyProperty.Register{attachedModifier}(");

								if (getAttached == null)
								{
									b.AppendLineInvariant($"\tnameof({propertyName}), typeof({propertyDisplayType}), ");
								}
								else
								{
									//attached properties do not have a corresponding property
									b.AppendLineInvariant($"\t\"{propertyName}\", typeof({propertyDisplayType}), ");
								}

								b.AppendLineInvariant($"\ttypeof({property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}), ");
								b.AppendLineInvariant($"\tnew FrameworkPropertyMetadata(default({propertyDisplayType})));");
							}
							else
							{
								b.AppendLineInvariant($"// Generating stub {property.Name} which has no C# getter");
								b.AppendLineInvariant($"internal static object {property.Name} {{{{ get; }}}}");
							}
						}
						else if (
							!property.IsStatic
							&& property.ContainingType.GetMembers(property.Name + "Property").Any()
						)
						{
							using (b.BlockInvariant($"public {staticQualifier} {MapUWPTypes(SanitizeType(property.Type))} {property.Name}"))
							{
								if (property.GetMethod != null)
								{
									using (b.BlockInvariant($"get"))
									{
										b.AppendLineInvariant($"return ({MapUWPTypes(SanitizeType(property.Type))})this.GetValue({property.Name}Property);");
									}
								}

								if (property.SetMethod != null)
								{
									using (b.BlockInvariant($"set"))
									{
										b.AppendLineInvariant($"this.SetValue({property.Name}Property, value);");
									}
								}
							}
						}
						else
						{
							using (b.BlockInvariant($"public {staticQualifier} {MapUWPTypes(SanitizeType(property.Type))} {property.Name}"))
							{
								if (property.GetMethod != null)
								{
									using (b.BlockInvariant($"get"))
									{
										BuildNotImplementedException(b, property, true);
									}
								}

								if (property.SetMethod != null)
								{
									using (b.BlockInvariant($"set"))
									{
										BuildNotImplementedException(b, property, false);
									}
								}
							}
						}
					}

					b.AppendLineInvariant($"#endif");
				}
				else
				{
					b.AppendLineInvariant($"// Skipping already declared property {property.Name}");
				}
			}
		}

		private bool SkipProperty(IPropertySymbol property)
		{
			if (property.ContainingType.Name == "WebView")
			{
				switch (property.Name)
				{
					case "XYFocusRight":
					case "XYFocusLeft":
					case "XYFocusDown":
					case "XYFocusUp":
					case "XYFocusRightProperty":
					case "XYFocusLeftProperty":
					case "XYFocusDownProperty":
					case "XYFocusUpProperty":
						return true;
				}
			}

			if (property.ContainingType.Name == "WebView2")
			{
				switch (property.Name)
				{
					case "CoreWebView2":
						return true;
				}
			}

			if (property.ContainingType.Name == "UIElement")
			{
				switch (property.Name)
				{
					case "Opacity":
					case "OpacityProperty":
					case "Visibility":
					case "VisibilityProperty":
					case "IsHitTestVisible":
					case "IsHitTestVisibleProperty":
					case "Transitions":
					case "TransitionsProperty":
					case "RenderTransform":
					case "RenderTransformProperty":
					case "RenderTransformOrigin":
					case "RenderTransformOriginProperty":
						return true;
				}
			}

			if (property.ContainingType.Name == "FrameworkElement")
			{
				switch (property.Name)
				{
					// This is ignored until DataContext becomes an actual DP.
					case "DataContext":
					case "DataContextProperty":
						return true;
				}
			}

			if (property.ContainingType.Name == "RelativeSource")
			{
				switch (property.Name)
				{
					case "TemplatedParent":
						return true;
				}
			}

			return false;
		}

		private object SanitizeParameter(string name)
			=> name switch
			{
				"event" => "@event",
				"object" => "@object",
				_ => name
			};

		private string SanitizeType(ITypeSymbol type)
		{
			var result = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

			return result;
		}

		private static string MapUWPTypes(string typeName)
		{
			return typeName switch
			{
				//"global::Windows.Foundation.Collections.IIterable" => "global::System.Collections.Generic.IEnumerable",
				//"global::Windows.Foundation.IAsyncOperation" => "global::System.Threading.Tasks.Task",
				//"global::Windows.Foundation.IAsyncAction" => "global::System.Threading.Tasks.Task",
				//"global::Windows.Foundation.Collections.IMapView" => "global::System.Collections.Generic.IReadOnlyDictionary",
				//"global::Windows.Foundation.Collections.IMap" => "global::System.Collections.Generic.IDictionary",
				//"global::Windows.Foundation.IReference" => "global::System.Nullable",
				//"global::Windows.Foundation.Collections.IVectorView" => "global::System.Collections.Generic.IReadOnlyList",
				//"global::Windows.Foundation.Collections.IVector" => "global::System.Collections.Generic.IList",
				//"global::Windows.Foundation.DateTime" => "global::System.DateTimeOffset",
				//"global::Windows.Foundation.EventHandler" => "global::System.EventHandler",
				//"global::Windows.Foundation.TimeSpan" => "global::System.TimeSpan",
				//"global::Windows.Foundation.Collections.IKeyValuePair" => "global::System.Collections.Generic.KeyValuePair",
				//"global::Windows.UI.Xaml.Interop.TypeName" => "global::System.Type",
				//"global::Windows.Foundation.Uri" => "global::System.Uri",
				//"global::Windows.Foundation.ICloseable" => "global::System.IDisposable",
				"global::Windows.UI.Xaml.Input.ICommand" => "global::System.Windows.Input.ICommand",
				"global::Microsoft.UI.Xaml.Input.ICommand" => "global::System.Windows.Input.ICommand",
				"global::Microsoft.UI.Xaml.Interop.INotifyCollectionChanged" => "global::System.Collections.Specialized.INotifyCollectionChanged",
				"global::Microsoft.UI.Xaml.Data.INotifyPropertyChanged" => "global::System.ComponentModel.INotifyPropertyChanged",
				"global::Microsoft.UI.Xaml.Data.PropertyChangedEventHandler" => "global::System.ComponentModel.PropertyChangedEventHandler",
				_ => typeName,
			};
		}

		private IEnumerable<ISymbol> GetNonGeneratedMembers(ITypeSymbol symbol, string name)
		{
			var current = symbol
				?.GetMembers(name)
				.Where(m => m.Locations.None(l => l.SourceTree?.FilePath?.Contains("\\Generated\\") ?? false)) ?? new ISymbol[0];

			foreach (var memberSymbol in current)
			{
				yield return memberSymbol;
			}

			if (
				symbol?.BaseType != null
				&& !SymbolEqualityComparer.Default.Equals(symbol.BaseType, _iOSBaseSymbol)
				&& !SymbolEqualityComparer.Default.Equals(symbol.BaseType, _androidBaseSymbol)
				&& !SymbolEqualityComparer.Default.Equals(symbol.BaseType, _macOSBaseSymbol)
			)
			{
				foreach (var memberSymbol in GetNonGeneratedMembers(symbol.BaseType, name))
				{
					yield return memberSymbol;
				}
			}
		}

		private IMethodSymbol FindMatchingMethod(ITypeSymbol symbol, IMethodSymbol sourceMethod)
		{
			var q = GetNonGeneratedMembers(symbol, sourceMethod.Name)?.OfType<IMethodSymbol>();

			if (sourceMethod?.ContainingSymbol?.Name == "RelativePanel")
			{
				return q.FirstOrDefault();
			}
			else
			{
				return q
					.FirstOrDefault(m =>
					{
						var sourceParams = sourceMethod
							.Parameters
							.Select(p => p.Type.ToDisplayString(NullableFlowState.None));
						var targetParams = m
								.Parameters
								.Select(p => p.Type.ToDisplayString(NullableFlowState.None));
						return sourceParams.SequenceEqual(targetParams);
					}
					);
			}
		}

		static Dictionary<(string projectFile, string targetFramework), Compilation> _projects
			= new Dictionary<(string projectFile, string targetFramework), Compilation>();

		private static Compilation LoadProject(string projectFile, string targetFramework = null)
		{
			var key = (projectFile, targetFramework);

			if (_projects.TryGetValue(key, out var compilation))
			{
				Console.WriteLine($"Using cached compilation for {projectFile} and {targetFramework}");
				return compilation;
			}

			return _projects[key] = InnerLoadProject(projectFile, targetFramework);
		}

		private static Compilation InnerLoadProject(string projectFile, string targetFramework = null)
		{
			Console.WriteLine($"Loading for {targetFramework}: {Path.GetFileName(projectFile)}");

			var properties = new Dictionary<string, string>
							{
								// { "VisualStudioVersion", "15.0" },
								// { "Configuration", "Debug" },
								//{ "BuildingInsideVisualStudio", "true" },
								{ "SkipUnoResourceGeneration", "true" }, // Required to avoid loading a non-existent task
								{ "DocsGeneration", "true" }, // Detect that source generation is running
								{ "LangVersion", "8.0" },
								//{ "DesignTimeBuild", "true" },
								//{ "UseHostCompilerIfAvailable", "false" },
								//{ "UseSharedCompilation", "false" },
							};

			if (targetFramework != null)
			{
				properties.Add("TargetFramework", targetFramework);
			}

			var ws = MSBuildWorkspace.Create(properties);

			ws.LoadMetadataForReferencedProjects = true;

			ws.WorkspaceFailed +=
				(s, e) => Console.WriteLine(e.Diagnostic.ToString());

			var project = ws.OpenProjectAsync(projectFile).Result;

			var generatedDocs = project
				.Documents
				.Where(d => d.FilePath.Contains("\\Generated\\"))
				.Select(d => d.Id)
				.ToArray();

			if (generatedDocs.Any())
			{
				foreach (var doc in generatedDocs)
				{
					project = project.RemoveDocument(doc);
				}
			}

			var metadataLessProjects = ws
				.CurrentSolution
				.Projects
				.Where(p => p.MetadataReferences.None())
				.ToArray();

			if (metadataLessProjects.Any())
			{
				// In this case, this may mean that Rolsyn failed to execute some msbuild task that loads the
				// references in a UWA project (or NuGet 3.0+ with project.json, more specifically). For these
				// projects, references are materialized through a task using a output parameter that injects
				// "References" nodes. If this task fails, no references are loaded, and simple type resolution
				// such "int?" may fail.

				// Additionally, it may happen that projects are loaded using the callee's Configuration/Platform, which
				// may not exist in all projects. This can happen if the project does not have a proper
				// fallback mechanism in place.

				SourceGeneration.Host.ProjectLoader.LoadProjectDetails(projectFile, "Debug");

				throw new InvalidOperationException(
					$"The project(s) {metadataLessProjects.Select(p => p.Name).JoinBy(",")} did not provide any metadata reference. " +
					"This may be due to an invalid path, such as $(SolutionDir) being used in the csproj; try using relative paths instead." +
					"This may also be related to a missing default configuration directive. Refer to the Uno.SourceGenerator Readme.md file for more details."
				);
			}

			project = RegisterGenericHelperTypes(project);

			return project
					.GetCompilationAsync().Result;
		}

		private static Microsoft.CodeAnalysis.Project RegisterGenericHelperTypes(Microsoft.CodeAnalysis.Project project)
		{
			var sb = new StringBuilder();

			for (int i = 0; i < 10; i++)
			{
				sb.AppendLine($"class __helper{i}__ {{}}");
			}

			return project.AddDocument("AdditionalGenericNames", sb.ToString()).Project;
		}

		public static IEnumerable<INamedTypeSymbol> GetNamespaceTypes(INamespaceSymbol sym)
		{
			foreach (var child in sym.GetTypeMembers())
			{
				yield return child;
			}

			foreach (var ns in sym.GetNamespaceMembers())
			{
				foreach (var child2 in GetNamespaceTypes(ns))
				{
					yield return child2;
				}
			}
		}


		private static void RegisterAssemblyLoader()
		{
			// Force assembly loader to consider siblings, when running in a separate appdomain.
			ResolveEventHandler localResolve = (s, e) =>
			{
				if (e.Name == "Mono.Runtime")
				{
					// Roslyn 2.0 and later checks for the presence of the Mono runtime
					// through this check.
					return null;
				}

				var assembly = new AssemblyName(e.Name);
				var basePath = Path.GetDirectoryName(new Uri(typeof(Generator).Assembly.CodeBase).LocalPath);

				Console.WriteLine($"Searching for [{assembly}] from [{basePath}]");

				// Ignore resource assemblies for now, we'll have to adjust this
				// when adding globalization.
				if (assembly.Name.EndsWith(".resources"))
				{
					return null;
				}

				// Lookup for the highest version matching assembly in the current app domain.
				// There may be an existing one that already matches, even though the
				// fusion loader did not find an exact match.
				var loadedAsm = (
									from asm in AppDomain.CurrentDomain.GetAssemblies()
									where asm.GetName().Name == assembly.Name
									orderby asm.GetName().Version descending
									select asm
								).ToArray();

				if (loadedAsm.Length > 1)
				{
					var duplicates = loadedAsm
						.Skip(1)
						.Where(a => a.GetName().Version == loadedAsm[0].GetName().Version)
						.ToArray();

					if (duplicates.Length != 0)
					{
						Console.WriteLine($"Selecting first occurrence of assembly [{e.Name}] which can be found at [{duplicates.Select(d => d.CodeBase).JoinBy("; ")}]");
					}

					return loadedAsm[0];
				}
				else if (loadedAsm.Length == 1)
				{
					return loadedAsm[0];
				}

				Assembly LoadAssembly(string filePath)
				{
					if (File.Exists(filePath))
					{
						try
						{
							var output = Assembly.LoadFrom(filePath);

							Console.WriteLine($"Loaded [{output.GetName()}] from [{output.CodeBase}]");

							return output;
						}
						catch (Exception ex)
						{
							Console.WriteLine($"Failed to load [{assembly}] from [{filePath}]", ex);
							return null;
						}
					}
					else
					{
						return null;
					}
				}

				var paths = new[] {
					Path.Combine(basePath, assembly.Name + ".dll"),
					Path.Combine(MSBuildBasePath, assembly.Name + ".dll"),
				};

				return paths
					.Select(LoadAssembly)
					.Where(p => p != null)
					.FirstOrDefault();
			};

			AppDomain.CurrentDomain.AssemblyResolve += localResolve;
			AppDomain.CurrentDomain.TypeResolve += localResolve;
		}
	}
}
