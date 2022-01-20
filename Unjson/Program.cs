using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NJsonSchema;
using NJsonSchema.References;

namespace Unjson
{
	class Program
	{
		static void Main(string[] args)
		{
			var json = File.ReadAllText(args[0]);
			
			var rootClassName = "Root";

			// generate class candidates from schema
			var schema = JsonSchema.FromSampleJson(json);
			JsonSchemaReferenceUtilities.UpdateSchemaReferencePaths(schema, false, JsonSchema.CreateJsonSerializerContractResolver(SchemaType.JsonSchema));
			var classes = GenerateClasses(schema, rootClassName);

			// collect examples
			var doc = JsonDocument.Parse(json);
			CollectExamples(schema.Definitions, schema, classes, rootClassName, doc.RootElement);
			
			// TODO: allow loading multiple json instances to refine type values and examples

			// specialize property types and finalize classes
			var finalizedClasses = classes.Values.Select(FinalizeClassCandidate).ToArray();

			using var sw = new StringWriter();
			GenerateSources(finalizedClasses, sw);
			
			Console.WriteLine(sw.ToString());
		}

		private static void GenerateSources(GeneratedClass[] finalizedClasses, TextWriter stringWriter)
		{
			foreach (var (name, props) in finalizedClasses)
			{
				stringWriter.WriteLine($"public class {name}");
				stringWriter.Write("{");

				foreach (var (propName, jsonName, type) in props)
				{
					stringWriter.WriteLine();
					stringWriter.WriteLine($"\t[JsonPropertyName(\"{jsonName}\")] public {type} {propName} {{ get; set; }}");
				}
				
				stringWriter.WriteLine("}");
				stringWriter.WriteLine();
			}
		}

		private static GeneratedClass FinalizeClassCandidate(GeneratedClassCandidate c)
		{
			var props = c.Properties.Values.Select(FinalizePropertyCandidate).ToArray();
			return new GeneratedClass(c.JsonName, props);
		}

		private static GeneratedProperty FinalizePropertyCandidate(GeneratedPropertyCandidate p)
		{
			var type = SpecializeType(p.JsonType, p.Examples);

			if (p.ArrayDimensions > 0)
			{
				const string arrayPattern = "List<$>";
				for (var i = 0; i < p.ArrayDimensions; i++)
					type = arrayPattern.Replace("$", type);
			}

			var name = FinalizePropertyName(p.JsonName);
			return new GeneratedProperty(name, p.JsonName, type);
		}

		private static string FinalizePropertyName(string jsonName)
		{
			var sb = new StringBuilder();

			var wordStart = true;
			foreach (var c in jsonName)
			{
				// Word isn't lowercase, uppercase, or (a digit as the first character)
				if (c is (< 'a' or > 'z') and (< 'A' or > 'Z') && (sb.Length == 0 || c is < '0' or > '9'))
				{
					wordStart = true;
					continue;
				}

				sb.Append(wordStart ? char.ToUpper(c) : c);
				
				wordStart = false;
			}
			
			return sb.ToString();
		}

		private static string SpecializeType(string typeHint, List<string> examples)
		{
			if (!Enum.TryParse(typeHint, out JsonObjectType jot))
				// Class name
				return typeHint;

			switch (jot)
			{
				case JsonObjectType.Boolean:
					return SpecializeBoolean(examples);
				case JsonObjectType.Integer:
					return SpecializeInteger(examples);
				case JsonObjectType.Number:
					return SpecializeNumber(examples);
				case JsonObjectType.String:
					return "string";
				case JsonObjectType.None:
					return "object"; // TODO: warn that None type was found
				case JsonObjectType.Array:
				case JsonObjectType.Object:
					throw new InvalidOperationException();
				case JsonObjectType.Null:
				case JsonObjectType.File:
					throw new NotSupportedException();
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private static string SpecializeNumber(List<string> examples)
		{
			var nullable = HasAnyNullOrUndefined(examples);
			var type = "float";

			foreach (var example in examples)
			{
				if (IsNullOrUndefined(example))
					continue;

				if (!float.TryParse(example, out var f))
					throw new InvalidDataException();

				if (f.ToString(CultureInfo.InvariantCulture) == example) continue;

				type = "double";
				break;
			}

			return type + (nullable ? "?" : "");
		}

		private static string SpecializeInteger(List<string> examples)
		{
			var nullable = HasAnyNullOrUndefined(examples);
			var type = "int";

			if (examples.Any(s => !IsNullOrUndefined(s) && !int.TryParse(s, out _)))
			{
				if (examples.Any(s => !IsNullOrUndefined(s) && !long.TryParse(s, out _)))
					type = "ulong";
				else
					type = "long";
			}

			return type + (nullable ? "?" : "");
		}

		private static string SpecializeBoolean(List<string> examples)
		{
			if (HasAnyNullOrUndefined(examples))
				return "bool?";
			return "bool";
		}

		private static bool HasAnyNullOrUndefined(List<string> examples)
		{
			// Undefined examples are stored as {null}
			// Examples which explicitly define the value as null are stored as "null"
			return examples.Any(IsNullOrUndefined);
		}

		private static bool IsNullOrUndefined(string s)
		{
			return s is null or "null";
		}

		private static void CollectExamples(IDictionary<string, JsonSchema> defs, JsonSchema schema, Dictionary<string, GeneratedClassCandidate> classes, string elementClass, JsonElement element)
		{
			var genClass = classes[elementClass];

			foreach (var (propName, propValue) in schema.Properties)
			{
				var (isPrimitive, type, _) = GetTypeFor(propValue);

				if (type == "None" || !element.TryGetProperty(propName, out var data))
				{
					if (isPrimitive)
						genClass.Properties[propName].Examples.Add(null);
					continue;
				}

				switch (data.ValueKind)
				{
					case JsonValueKind.Object:
					{
						CollectExamples(defs, defs[type], classes, type, data);
						break;
					}
					case JsonValueKind.Array:
					{
						foreach (var arrEl in data.EnumerateArray())
						{
							if (isPrimitive)
								genClass.Properties[propName].Examples.Add(arrEl.GetRawText());
							else
								CollectExamples(defs, defs[type], classes, type, arrEl);
						}

						break;
					}
					case JsonValueKind.String: // TODO: detect DateTime, GUID, etc
					case JsonValueKind.Number:
					case JsonValueKind.True:
					case JsonValueKind.False:
					case JsonValueKind.Null:
						if (isPrimitive)
							genClass.Properties[propName].Examples.Add(data.GetRawText());
						break;
					case JsonValueKind.Undefined:
						throw new InvalidOperationException();
					default:
						throw new NotSupportedException();
				}
			}
		}

		private static Dictionary<string, GeneratedClassCandidate> GenerateClasses(JsonSchema schema, string className)
		{
			var generatedClasses = new Dictionary<string, GeneratedClassCandidate>();

			if (schema.Type == JsonObjectType.Array)
				schema = schema.Item;
			
			if (schema.Type != JsonObjectType.Object)
				return generatedClasses;
			
			var props = new Dictionary<string, GeneratedPropertyCandidate>();

			foreach (var (name, prop) in schema.Properties)
			{
				var (_, type, dims) = GetTypeFor(prop);
				props[name] = new GeneratedPropertyCandidate(name, type, dims, new List<string>());
			}

			generatedClasses[className] = new GeneratedClassCandidate(className, props);

			foreach (var (name, definition) in schema.Definitions)
				foreach (var (k, v) in GenerateClasses(definition, name))
					generatedClasses[k] = v;

			return generatedClasses;
		}

		private static (bool isPrimitive, string type, int arrayDimensions) GetTypeFor(JsonSchema prop)
		{
			if (prop.IsArray)
			{
				var (isPrimitive, childType, childArrayDim) = GetTypeFor(prop.Item);
				return (isPrimitive, childType, childArrayDim + 1);
			}

			if (prop.HasReference)
			{
				if (prop.Reference.Type != JsonObjectType.Object)
					return (true, prop.Reference.Type.ToString(), 0);

				return (false, ((IJsonReferenceBase)prop).ReferencePath.Replace("#/definitions/", ""), 0);
			}

			return (true, prop.Type.ToString(), 0);
		}
	}

	internal record GeneratedClass(string Name, GeneratedProperty[] Properties);

	internal record GeneratedProperty(string Name, string JsonName, string Type);

	internal record GeneratedClassCandidate(string JsonName, Dictionary<string, GeneratedPropertyCandidate> Properties);

	internal record GeneratedPropertyCandidate(string JsonName, string JsonType, int ArrayDimensions, List<string> Examples);
}