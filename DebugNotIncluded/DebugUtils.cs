﻿/*
 * Copyright 2021 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using Harmony;
using KMod;
using PeterHan.PLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PeterHan.DebugNotIncluded {
	/// <summary>
	/// Utility methods for Debug Not Included.
	/// </summary>
	internal static class DebugUtils {
		/// <summary>
		/// Anchors the subelement to its middle left.
		/// </summary>
		internal static readonly Vector2 ANCHOR_MID_LEFT = new Vector2(0.0f, 0.5f);

		/// <summary>
		/// The relative folder in each mod where archived versions are stored.
		/// </summary>
		internal const string ARCHIVED_VERSIONS_FOLDER = "archived_versions";

		/// <summary>
		/// The margin inside each button.
		/// </summary>
		internal static readonly RectOffset BUTTON_MARGIN = new RectOffset(10, 10, 7, 7);

		/// <summary>
		/// Finds only declared members of a class of any visibility and static/instance.
		/// </summary>
		private const BindingFlags DEC_FLAGS = PUBLIC_PRIVATE | INSTANCE_STATIC | BindingFlags.
			DeclaredOnly;

		/// <summary>
		/// Binding flags used to find instance and static members of a class.
		/// </summary>
		private const BindingFlags INSTANCE_STATIC = BindingFlags.Instance | BindingFlags.
			Static;

		/// <summary>
		/// The mod information file name used for archived versions.
		/// </summary>
		private const string MOD_INFO_FILENAME = "mod_info.yaml";

		/// <summary>
		/// Binding flags used to find members in a class of any visibility.
		/// </summary>
		private const BindingFlags PUBLIC_PRIVATE = BindingFlags.Public |
			BindingFlags.NonPublic;

		/// <summary>
		/// The pattern to match for generic types.
		/// </summary>
		private static readonly Regex GENERIC_TYPE = new Regex(@"([^<\[]+)[<\[](.+)[>\]]",
			RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

		/// <summary>
		/// The pattern to match for patched methods generated by Harmony.
		/// </summary>
		private static readonly Regex PATCHED_METHOD = new Regex(@"^(.+)_Patch([0-9]+)$",
			RegexOptions.CultureInvariant | RegexOptions.Compiled);

		/// <summary>
		/// Prestores a list of shorthand types used by Mono in stack traces.
		/// </summary>
		private static readonly IDictionary<string, Type> SHORTHAND_TYPES =
			new Dictionary<string, Type> {
				{ "bool", typeof(bool) }, { "byte", typeof(byte) }, { "sbyte", typeof(sbyte) },
				{ "char", typeof(char) }, { "decimal", typeof(decimal) },
				{ "double", typeof(double) }, { "float", typeof(float) },
				{ "int", typeof(int) }, { "uint", typeof(uint) }, { "long", typeof(long) },
				{ "ulong", typeof(ulong) }, { "short", typeof(short) },
				{ "ushort", typeof(ushort) }, { "object", typeof(object) },
				{ "string", typeof(string) }
			};

		/// <summary>
		/// The Action used when "UI Debug" is pressed.
		/// </summary>
		internal static PAction UIDebugAction { get; private set; }

		/// <summary>
		/// This method was adapted from Mono at https://github.com/mono/mono/blob/master/mcs/class/corlib/System.Diagnostics/StackTrace.cs
		/// which is available under the MIT License.
		/// </summary>
		internal static void AppendMethod(StringBuilder message, MethodBase method) {
			var declaringType = method.DeclaringType;
			// If this method was declared in a generic type, choose the right method to log
			if (declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition) {
				declaringType = declaringType.GetGenericTypeDefinition();
				foreach (var m in declaringType.GetMethods(DEC_FLAGS))
					if (m.MetadataToken == method.MetadataToken) {
						method = m;
						break;
					}
			}
			// Type and name
			message.Append(declaringType.ToString());
			message.Append(".");
			message.Append(method.Name);
			// If the method itself is generic, use its definition
			if (method.IsGenericMethod && (method is MethodInfo methodInfo)) {
				if (!method.IsGenericMethodDefinition)
					method = methodInfo.GetGenericMethodDefinition();
				message.Append("[");
				message.Append(method.GetGenericArguments().Join());
				message.Append("]");
			}
			// Parameters
			message.Append(" (");
			var parameters = method.GetParameters();
			for (int i = 0; i < parameters.Length; ++i) {
				var paramType = parameters[i].ParameterType;
				string name = parameters[i].Name;
				if (i > 0)
					message.Append(", ");
				// Parameter type, use the definition if possible
				if (paramType.IsGenericType && !paramType.IsGenericTypeDefinition)
					paramType = paramType.GetGenericTypeDefinition();
				message.Append(paramType.ToString());
				// Parameter name
				if (!string.IsNullOrEmpty(name)) {
					message.Append(" ");
					message.Append(name);
				}
			}
			message.Append(")");
		}

		/// <summary>
		/// Finds the best match for the specified method parameters types.
		/// </summary>
		/// <param name="candidates">The methods with the required name.</param>
		/// <param name="paramTypes">The parameter types that must be matched.</param>
		/// <returns>The best match found, or null if all matches sucked.</returns>
		internal static MethodBase BestEffortMatch(IEnumerable<MethodBase> candidates,
				Type[] paramTypes) {
			MethodBase method = null, firstMatch = null;
			int bestMatch = -1, n = 0, originalLength = paramTypes.Length;
			foreach (var candidate in candidates) {
				// Argument count must match, with <object> as first parameter for instance
				var parameters = candidate.GetParameters();
				var checkTypes = StripThisObject(paramTypes, !candidate.IsStatic);
				int nArgs = checkTypes.Length;
				if (parameters.Length == nArgs) {
					int matched = (originalLength == nArgs) ? 0 : 1;
					// Count argument types which match exactly
					for (int i = 0; i < nArgs; i++)
						if (checkTypes[i] == parameters[i].ParameterType)
							matched++;
					if (matched > bestMatch) {
						bestMatch = matched;
						method = candidate;
					}
				}
				firstMatch = candidate;
				n++;
			}
			if (method == null && n < 2)
				method = firstMatch;
			return method;
		}

		/// <summary>
		/// Gets the mod that most recently appeared on the provided call stack.
		/// </summary>
		/// <param name="stackTrace">The call stack of the exception thrown.</param>
		/// <returns>The mod that is most likely at fault, or null if no mods appear on the stack.</returns>
		internal static ModDebugInfo GetFirstModOnCallStack(this StackTrace stackTrace) {
			ModDebugInfo mod = null;
			if (stackTrace != null) {
				var registry = ModDebugRegistry.Instance;
				int n = stackTrace.FrameCount;
				// Search for first method that has a mod in the registry
				for (int i = 0; i < n && mod == null; i++) {
					var method = stackTrace.GetFrame(i)?.GetMethod();
					if (method != null)
						mod = registry.OwnerOfType(method.DeclaringType);
				}
			}
			return mod;
		}

		/// <summary>
		/// A slimmed down and public version of Mod.GetModInfoForFolder to read
		/// archived version information.
		/// </summary>
		/// <param name="modInfo">The mod to query.</param>
		/// <param name="archivedPath">The relative path to the mod base folder.</param>
		/// <returns>The version information for that particular archived version.</returns>
		internal static Mod.PackagedModInfo GetModInfoForFolder(Mod modInfo,
				string archivedPath) {
			var items = new List<FileSystemItem>(32);
			modInfo.file_source.GetTopLevelItems(items, archivedPath);
			Mod.PackagedModInfo result = null;
			foreach (var item in items)
				if (item.type == FileSystemItem.ItemType.File && item.name ==
						MOD_INFO_FILENAME) {
					// No option but to read into memory, Klei API has no stream version
					string contents = modInfo.file_source.Read(Path.Combine(archivedPath,
						MOD_INFO_FILENAME));
					if (!string.IsNullOrEmpty(contents))
						result = Klei.YamlIO.Parse<Mod.PackagedModInfo>(contents, default);
					break;
				}
			return result;
		}

		/// <summary>
		/// If the provided method is a Harmony patched method, finds the original method
		/// that was patched.
		/// 
		/// The returned method might not be callable, but it can be used to figure out the
		/// patches that were applied.
		/// </summary>
		/// <param name="patched">The potentially patched method.</param>
		/// <returns>The original method which was targeted by the patch.</returns>
		internal static MethodBase GetOriginalMethod(MethodBase patched) {
			MethodBase newMethod = null;
			if (patched == null)
				throw new ArgumentNullException("patched");
			// If the method ends with "_PatchNNN", then strip that
			var match = PATCHED_METHOD.Match(patched.Name);
			if (match.Success) {
				var parameters = patched.GetParameters();
				string name = match.Groups[1].Value;
				// Copy parameter types to array
				int n = parameters.Length;
				var paramTypes = new Type[n];
				for (int i = 0; i < n; i++)
					paramTypes[i] = parameters[i].ParameterType;
				// Look for the new method that does not have the _Patch suffix
				try {
					newMethod = patched.DeclaringType.GetMethod(name, PUBLIC_PRIVATE |
						BindingFlags.Instance, null, StripThisObject(paramTypes, true), null);
				} catch (AmbiguousMatchException) { }
				if (newMethod == null)
					try {
						newMethod = patched.DeclaringType.GetMethod(name, PUBLIC_PRIVATE |
							BindingFlags.Static, null, paramTypes, null);
					} catch (AmbiguousMatchException) { }
				if (newMethod != null)
					// Found a better match!
					patched = newMethod;
			}
			return patched;
		}

		/// <summary>
		/// Gets information about what patched the method.
		/// </summary>
		/// <param name="method">The method to check.</param>
		/// <param name="message">The location where the message will be stored.</param>
		internal static void GetPatchInfo(MethodBase method, StringBuilder message) {
			var patches = ModDebugRegistry.Instance.DebugInstance.GetPatchInfo(method);
			if (patches != null) {
				GetPatchInfo(patches.Prefixes, "Prefixed", message);
				GetPatchInfo(patches.Postfixes, "Postfixed", message);
				GetPatchInfo(patches.Transpilers, "Transpiled", message);
			}
		}

		/// <summary>
		/// Gets information about patches on a method.
		/// </summary>
		/// <param name="patches">The patches applied to the method.</param>
		/// <param name="verb">The verb to describe these patches.</param>
		/// <param name="message">The location where the message will be stored.</param>
		private static void GetPatchInfo(IEnumerable<Patch> patches, string verb,
				StringBuilder message) {
			var registry = ModDebugRegistry.Instance;
			ModDebugInfo info;
			foreach (var patch in patches) {
				string owner = patch.owner;
				var method = patch.patch;
				// Try to resolve to the friendly mod name
				if ((info = registry.GetDebugInfo(owner)) != null)
					owner = info.ModName;
				message.AppendFormat("    {4} by {0}[{1:D}] from {2}.{3}", owner, patch.index,
					method.DeclaringType.FullName, method.Name, verb);
				message.AppendLine();
			}
		}

		/// <summary>
		/// Returns whether the type is included in the base game assemblies.
		/// </summary>
		/// <param name="type">The type to check.</param>
		/// <returns>true if it is included in the base game, or false otherwise.</returns>
		internal static bool IsBaseGameType(this Type type) {
			bool isBase = false;
			if (type != null) {
				var assembly = type.Assembly;
				isBase = assembly == typeof(Mod).Assembly || assembly == typeof(Tag).Assembly;
			}
			return isBase;
		}

		/// <summary>
		/// Finds the type for the given name using the method that Unity/Mono uses to report
		/// types internally.
		/// </summary>
		/// <param name="typeName">The type name.</param>
		/// <returns>The types for each type name, or null if no type could be resolved.</returns>
		internal static Type GetTypeByUnityName(this string typeName) {
			Type type = null;
			if (!string.IsNullOrEmpty(typeName) && !SHORTHAND_TYPES.TryGetValue(typeName,
					out type)) {
				// Generic type?
				var match = GENERIC_TYPE.Match(typeName);
				if (match.Success) {
					var parameters = match.Groups[2].Value.Split(',');
					int nParams = parameters.Length;
					// Convert parameters to types
					var paramTypes = new Type[nParams];
					for (int i = 0; i < nParams; i++)
						paramTypes[i] = parameters[i].GetTypeByUnityName();
					// Genericize it
					var baseType = PPatchTools.GetTypeSafe(RemoveBacktick(match.Groups[1].
						Value));
					if (baseType != null && baseType.IsGenericTypeDefinition)
						type = baseType.MakeGenericType(paramTypes);
					else
						type = baseType;
				} else
					type = PPatchTools.GetTypeSafe(RemoveBacktick(typeName));
			}
			return type;
		}

		/// <summary>
		/// Opens the output log file.
		/// </summary>
		internal static void OpenOutputLog() {
			// Ugly but true!
			var platform = Application.platform;
			string verString = Application.unityVersion;
			// Get the major Unity version
			if (verString != null && verString.Length > 4)
				verString = verString.Substring(0, 4);
			if (!uint.TryParse(verString, out uint unityYear))
				unityYear = 2000u;
			string path = "";
			switch (platform) {
			case RuntimePlatform.OSXPlayer:
			case RuntimePlatform.OSXEditor:
				// https://answers.unity.com/questions/1484445/how-do-i-find-the-player-log-file-from-code.html
				path = "~/Library/Logs/Unity/Player.log";
				break;
			case RuntimePlatform.LinuxEditor:
			case RuntimePlatform.LinuxPlayer:
				path = Path.Combine("~/.config/unity3d", Application.companyName, Application.
					productName, "Player.log");
				break;
			case RuntimePlatform.WindowsEditor:
			case RuntimePlatform.WindowsPlayer:
				path = Path.Combine(Environment.GetEnvironmentVariable("AppData"),
					"..", "LocalLow", Application.companyName, Application.productName,
					unityYear < 2019 ? "output_log.txt" : "Player.log");
				break;
			default:
				DebugLogger.LogWarning("Unable to open the output log on: {0}", platform);
				break;
			}
			if (!string.IsNullOrEmpty(path))
				Application.OpenURL(Path.GetFullPath(path));
		}

		/// <summary>
		/// Registers the UI debug action (default Alt+U) to dump UI element trees to the log.
		/// </summary>
		internal static void RegisterUIDebug() {
			UIDebugAction = PAction.Register("DebugNotIncluded.UIDebugAction",
				DebugNotIncludedStrings.INPUT_BINDINGS.DEBUG.SNAPSHOT, new PKeyBinding(
				KKeyCode.U, Modifier.Alt));
		}

		/// <summary>
		/// Removes the backtick and subsequent numbers from type names.
		/// </summary>
		/// <param name="typeName">The type name.</param>
		/// <returns>The name without the backtick suffix.</returns>
		private static string RemoveBacktick(string typeName) {
			int index = typeName.IndexOf('`');
			return index > 0 ? typeName.Substring(0, index) : typeName;
		}

		/// <summary>
		/// Removes "this" from the parameters if the first parameter is an object and
		/// instance is true.
		/// </summary>
		/// <param name="paramTypes">The parameter types.</param>
		/// <param name="instance">If true, the first parameter is removed if it is of
		/// type System.Object.</param>
		/// <returns>The updated parameter types.</returns>
		private static Type[] StripThisObject(Type[] paramTypes, bool instance) {
			if (paramTypes == null)
				throw new ArgumentNullException("parameters");
			Type[] types;
			int n = paramTypes.Length;
			if (instance && n > 0 && paramTypes[0] == typeof(object)) {
				// Instance method, remove first
				n--;
				types = new Type[n];
				for (int i = 0; i < n; i++)
					types[i] = paramTypes[i + 1];
			} else
				types = paramTypes;
			return types;
		}
	}
}
