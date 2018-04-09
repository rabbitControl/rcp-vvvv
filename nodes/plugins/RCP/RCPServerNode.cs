#region usings
using System;
using System.IO;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V2.Graph;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;
using VVVV.Core.Logging;

using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using V4 = System.Numerics.Vector4;

using RCP;
using RCP.Model;
using RCP.Transporter;
using RCP.Parameter;
using RCP.Protocol;

using Kaitai;

#endregion usings

namespace VVVV.Nodes
{
	#region PluginInfo
	[PluginInfo(Name = "Rabbit",
	Category = "RCP",
	AutoEvaluate = true,
	Help = "An RCP Server",
	Tags = "remote, server")]
	#endregion PluginInfo
	public class RCPRabbitNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable
	{
		#region fields & pins
		[Input("Host", IsSingle=true, DefaultString = "127.0.0.1")]
		public IDiffSpread<string> FHost; 
		
		[Input("Port", IsSingle=true, DefaultValue = 10000)]
		public IDiffSpread<int> FPort; 
		
		[Input("Update Enums", IsSingle=true, IsBang=true)]
		public ISpread<bool> FUpdateEnums; 
		
		[Output("Connection Count")]
		public ISpread<int> FConnectionCount;
		//public ISpread<byte> FOutput;
		
		[Import()]
		public ILogger FLogger;
		
		[Import()]
		public IHDEHost FHDEHost;
		
		RCPServer FRCPServer;
		WebsocketServerTransporter FTransporter;
		Dictionary<string, IPin2> FCachedPins = new Dictionary<string, IPin2>();
		Dictionary<string, List<IParameter>> FParameters = new Dictionary<string, List<IParameter>>();
		
		List<IParameter> FParameterQueue = new List<IParameter>();
		#endregion fields & pins
		  
		public RCPRabbitNode()
		{ 
			//initialize the RCP Server
			FRCPServer = new RCPServer();
  
			//subscribe to the parameter-updated event
			FRCPServer.ParameterUpdated = ParameterUpdated;
		}
		
		public void OnImportsSatisfied()
		{
			FHDEHost.ExposedNodeService.NodeAdded += NodeAddedCB;
			FHDEHost.ExposedNodeService.NodeRemoved += NodeRemovedCB;
			
			GroupMap.GroupChanged += GroupChanged; 
			
			//FRCPServer.Log = (s) => FLogger.Log(LogType.Debug, "server: " + s);
			 
			//get initial list of exposed ioboxes
			foreach (var node in FHDEHost.ExposedNodeService.Nodes)
				NodeAddedCB(node);
		}
		
		private void GroupChanged(string parentId)
		{
			List<IParameter> parameters;
			if (FParameters.TryGetValue(parentId, out parameters))
			{
				//FLogger.Log(LogType.Debug, "ps: " + parentId);
				
				var newParent = GroupMap.GetName(parentId).ToRCPId();
				foreach (var param in parameters)
				{
					param.Parent = newParent;
					FRCPServer.UpdateParameter(param);
				}
			}
		}
		
		public void Dispose()
		{
			//unscubscribe from nodeservice
			FHDEHost.ExposedNodeService.NodeAdded -= NodeAddedCB;
			FHDEHost.ExposedNodeService.NodeRemoved -= NodeRemovedCB;
			
			GroupMap.GroupChanged -= GroupChanged; 
			
			//dispose the RCP server
			FLogger.Log(LogType.Debug, "Disposing the RCP Server");
			FRCPServer.Dispose();
			
			//clear cached pins
			FCachedPins.Clear();
			//FNodeToIdMap.Clear();
			
			FParameterQueue.Clear();
		}
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			if (FTransporter == null)
			{
				FTransporter = new WebsocketServerTransporter(FHost[0], FPort[0]);
				FRCPServer.AddTransporter(FTransporter);
			}
			
			if (FHost.IsChanged || FPort.IsChanged)
				FTransporter.Bind(FHost[0], FPort[0]);
			
			//TODO: subscribe to enum-changes on the host and update all related
			//parameters as changes happen, so a client can update its gui accordingly
			if (FUpdateEnums[0])
			{
				var enumPins = FCachedPins.Values.Where(v => v.Type == "Enumeration");
				
				foreach (var enumPin in enumPins)
					PinValueChanged(enumPin, null);
			}

			//process FParameterQueue
			//in order to handle all messages from main thread
			//since all COM-access is single threaded
			lock(FParameterQueue)
			{
				foreach (var param in FParameterQueue)
				{
					IPin2 pin;
					if (FCachedPins.TryGetValue(param.Id.ToIdString(), out pin))
						pin.Spread = RCP.Helpers.ValueToString(param);
				}
				FParameterQueue.Clear();
			}
			
			FConnectionCount[0] = FTransporter.ConnectionCount;
		}
		
		private void NodeAddedCB(INode2 node)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			pin.Changed += PinValueChanged;
			node.LabelPin.Changed += LabelChanged;
			var tagPin = node.FindPin("Tag");
			tagPin.Changed += TagChanged;
			//TODO: subscribe to subtype-pins here as well
			//tag
			//default, min, max, ...
			
			var id = IdFromPin(pin);
			FCachedPins.Add(id, pin);
			
			var parentId = ParentIdFromNode(node);
			var param = ParameterFromNode(node, parentId);
			AddParamToPatch(parentId, param);
			
			//OutputBytes(param);

			FRCPServer.AddParameter(param);
		}
		
		private void NodeRemovedCB(INode2 node)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			pin.Changed -= PinValueChanged;
			node.LabelPin.Changed -= LabelChanged;
			var tagPin = node.FindPin("Tag");
			tagPin.Changed -= TagChanged;
			
			var id = IdFromPin(pin);
			FCachedPins.Remove(id);
			
			var rcpId = id.ToRCPId();
			RemoveParamFromPatch(ParentIdFromNode(node), FRCPServer.GetParameter(rcpId));
			
			FRCPServer.RemoveParameter(rcpId);
		}
		
		private string IdFromPin(IPin2 pin)
		{
			var pinname = PinNameFromNode(pin.ParentNode);
			var pinpath = pin.ParentNode.GetNodePath(false) + "/" + pinname;
			return pinpath;
		}
		
		private string ParentIdFromNode(INode2 node)
		{
			var path = node.GetNodePath(false);
			var ids = path.Split('/'); 
			return string.Join("/", ids.Take(ids.Length-1));
		}
		
		private string PinNameFromNode(INode2 node)
		{ 
			string pinName = "";
			if (node.NodeInfo.Systemname == "IOBox (Value Advanced)")
			pinName = "Y Input Value";
			else if (node.NodeInfo.Systemname == "IOBox (String)")
			pinName = "Input String";
			else if (node.NodeInfo.Systemname == "IOBox (Color)")
			pinName = "Color Input";
			else if (node.NodeInfo.Systemname == "IOBox (Enumerations)")
			pinName = "Input Enum";
			else if (node.NodeInfo.Systemname == "IOBox (Node)")
			pinName = "Input Node";
			
			return pinName;
		}
		
		private IParameter ParameterFromNode(INode2 node, string parentId)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			var id = IdFromPin(pin).ToRCPId();
			
			IParameter parameter = null;
			
			var subtype = pin.SubType.Split(',').Select(s => s.Trim()).ToArray();
			var sliceCount = pin.SliceCount;
			
			switch(pin.Type)
			{
				case "Value": 
				{
					var dimensions = int.Parse(subtype[1]);
					//figure out the actual spreadcount
					//taking dimensions (ie. vectors) of value-spreads into account
					sliceCount /= dimensions;
					
					if (dimensions == 1)
					{
						int intStep = 0;
						float floatStep = 0;
						
						if (int.TryParse(subtype[5], out intStep)) //integer
						{
	                        var isbool = (subtype[3] == "0") && (subtype[4] == "1");
	                        if (isbool)
	                        {
	                        	var def = new BooleanDefinition();
	                        	def.Default = subtype[2] == "1";
	                        	
	                        	parameter = GetParameter<bool>(id, sliceCount, 1, def, pin, (p,i) => {return p[i] == "1";});
	                        }
							else
							{
								var def = new Integer32Definition();
								def.Default = RCP.Helpers.ParseInt(subtype[2]);
								def.Minimum = RCP.Helpers.ParseInt(subtype[3]);
								def.Maximum = RCP.Helpers.ParseInt(subtype[4]);
								def.MultipleOf = intStep;
								
								parameter = GetNumberParameter<int>(id, sliceCount, 1, def, pin, (p,i) => RCP.Helpers.GetInt(p,i));
							}
						}
						else if (float.TryParse(subtype[5], NumberStyles.Float, CultureInfo.InvariantCulture, out floatStep))
						{
//							uint precision = 0;
//							uint.TryParse(subtype[7], out precision);
							
							var def = new Float32Definition();
							def.Default = RCP.Helpers.ParseFloat(subtype[2]);
							def.Minimum = RCP.Helpers.ParseFloat(subtype[3]);
							def.Maximum = RCP.Helpers.ParseFloat(subtype[4]);
							def.MultipleOf = floatStep;
							
							parameter = GetNumberParameter<float>(id, sliceCount, 1, def, pin, (p,i) => RCP.Helpers.GetFloat(p,i));
						}
						
						switch (subtype[0])
						{
							case "Bang": parameter.Widget = new BangWidget(); break;
							case "Press": parameter.Widget = new PressWidget(); break;
							case "Toggle": parameter.Widget = new ToggleWidget(); break;
							case "Slider": parameter.Widget = new SliderWidget(); break;
							case "Endless": parameter.Widget = new EndlessWidget(); break;
						}
					}
					else if (dimensions == 2)
					{
						var def = new Vector2f32Definition();
						//TODO: parse 2d subtype when pin.Subtype supports it
						//var comps = subtype[2].Split(',');
						//FLogger.Log(LogType.Debug, subtype[2]);
						def.Default = new V2(RCP.Helpers.ParseFloat(subtype[2]));
						def.Minimum = new V2(RCP.Helpers.ParseFloat(subtype[3]));
						def.Maximum = new V2(RCP.Helpers.ParseFloat(subtype[4]));
						def.MultipleOf = new V2(RCP.Helpers.ParseFloat(subtype[5]));
							
						parameter = GetNumberParameter<V2>(id, sliceCount, 2, def, pin, (p,i) => RCP.Helpers.GetVector2(p,i));
					}
					else if (dimensions == 3)
					{
						var def = new Vector3f32Definition();
						//TODO: parse 3d subtype when pin.Subtype supports it
						//var comps = subtype[2].Split(',');
						//FLogger.Log(LogType.Debug, subtype[2]);
						def.Default = new V3(RCP.Helpers.ParseFloat(subtype[2]));
						def.Minimum = new V3(RCP.Helpers.ParseFloat(subtype[3]));
						def.Maximum = new V3(RCP.Helpers.ParseFloat(subtype[4]));
						def.MultipleOf = new V3(RCP.Helpers.ParseFloat(subtype[5]));
							
						parameter = GetNumberParameter<V3>(id, sliceCount, 3, def, pin, (p,i) => RCP.Helpers.GetVector3(p,i));
					}
					else if (dimensions == 4)
					{
						
					}
					break;
				}
				
				case "String": 
				{
					var schema = subtype[0].ToLower();
					if (schema == "filename" || schema == "directory")
					{
						var def = new UriDefinition();
						def.Default = subtype[1];
						def.Schema = "file";
						if (schema == "filename")
							def.Filter = subtype[2];
						
						if (sliceCount > 1)
						{
							var adef = new ArrayDefinition<string>(def, (uint)sliceCount);
							var param = (ArrayParameter<string>)ParameterFactory.CreateArrayParameter<string>(id, adef); 
							var values = new List<string>();
							for (int i=0; i<sliceCount; i++)
								values.Add(pin[i]);
							param.Value = values; 
							parameter = param;
						}
						else
						{
							var param = new UriParameter(id, def as IUriDefinition);
							
							var v = pin[0].TrimEnd('\\').Replace("\\", "/");
							if (schema == "directory")
								v += "/";
								
							param.Value = v;
							parameter = param;
						}
					}
					else if (schema == "url")
					{
						var def = new UriDefinition();
						def.Default = subtype[1];
						def.Schema = "http://";
						
						if (sliceCount > 1)
						{
							var adef = new ArrayDefinition<string>(def, (uint)sliceCount);
							var param = (ArrayParameter<string>)ParameterFactory.CreateArrayParameter<string>(id, adef); 
							var values = new List<string>();
							for (int i=0; i<sliceCount; i++)
								values.Add(pin[i]);
							param.Value = values; 
							parameter = param;
						}
						else
						{
							var param = new UriParameter(id, def as IUriDefinition);
							param.Value = pin[0];
							parameter = param;
						}
					}
					else 
					{
						var def = new StringDefinition();
						def.Default = subtype[1];
						
						if (sliceCount > 1)
						{
							var adef = new ArrayDefinition<string>(def, (uint)sliceCount);
							var param = (ArrayParameter<string>)ParameterFactory.CreateArrayParameter<string>(id, adef); 
							var values = new List<string>();
							for (int i=0; i<sliceCount; i++)
								values.Add(pin[i]);
							param.Value = values; 
							parameter = param;
						}
						else
						{
							var param = new StringParameter(id, def as IStringDefinition); 
							param.Value = pin[0];
							parameter = param;
						}
					}
					
					break;
				}
				
				case "Color":
	            {
		            /// colors: guiType, default, hasAlpha
	                bool hasAlpha = subtype[2].Trim() == "HasAlpha";
	                var def = new RGBADefinition();
	            	def.Default = Color.Red;
	
	                if (sliceCount > 1)
					{
						var adef = new ArrayDefinition<Color>(def, (uint)sliceCount);
						var param = (ArrayParameter<Color>)ParameterFactory.CreateArrayParameter<Color>(id, adef); 
						var values = new List<Color>();
						for (int i=0; i<sliceCount; i++)
							values.Add(RCP.Helpers.ParseColor(pin[i]));
						param.Value = values; 
						parameter = param;
					}
					else
					{
						var param = new RGBAParameter(id, def as IRGBADefinition); 
						param.Value = RCP.Helpers.ParseColor(pin[0]);
						parameter = param;
					} 
	            	
	            	break;
	            }
	            
				case "Enumeration":
	            {
		            /// enums: guiType, enumName, default
	                var enumName = subtype[1].Trim();
	            	var deflt = subtype[2].Trim();
	            	var def = GetEnumDefinition(enumName, deflt);
	            	var entries = def.Entries.ToList();
	            	
	            	if (sliceCount > 1)
					{
						var adef = new ArrayDefinition<ushort>(def, (uint)sliceCount);
						var param = (ArrayParameter<ushort>)ParameterFactory.CreateArrayParameter<ushort>(id, adef); 
						var values = new List<ushort>();
						for (int i=0; i<sliceCount; i++)
							values.Add((ushort)entries.IndexOf(pin[i]));
						param.Value = values; 
						parameter = param;
					}
					else
					{
						var param = new EnumParameter(id, def as IEnumDefinition); 
						param.Value = (ushort)entries.IndexOf(pin[0]);
						parameter = param;
					}
	            	
	            	break;
	            }
			}
			
			//no suitable parameter found?
			if (parameter == null)
			{
				parameter = new StringParameter(id);
				parameter.Label = "Unknown Value";
			}
			else
				parameter.Label = pin.ParentNode.LabelPin.Spread.Trim('|');
			
			//parent
			parameter.Parent = GroupMap.GetName(parentId).ToRCPId();
			
			//FLogger.Log(LogType.Debug, address + " - " + ParentMap.GetName(address));
			
			//order
			var bounds = node.GetBounds(BoundsType.Box);
			parameter.Order = bounds.X;
			
			//userdata
			var tag = node.FindPin("Tag");
            if (tag != null)
                parameter.Userdata = Encoding.UTF8.GetBytes(tag.Spread.Trim('|'));
			
			return parameter;
		}
		
		private EnumDefinition GetEnumDefinition(string enumName, string deflt)
		{
			var entryCount = EnumManager.GetEnumEntryCount(enumName);
            var entries = new List<string>();
            for (int i = 0; i < entryCount; i++)
                entries.Add(EnumManager.GetEnumEntryString(enumName, i));

            var def = new EnumDefinition();
            def.Default = (ushort) entries.IndexOf(deflt);
        	def.Entries = entries.ToArray();
			
			return def;
		}
		
		private void AddParamToPatch(string address, IParameter param)
		{
			if (!FParameters.ContainsKey(address))
				FParameters.Add(address, new List<IParameter>());
			
			FParameters[address].Add(param);
		}
		
		private void RemoveParamFromPatch(string address, IParameter param)
		{
			FParameters[address].Remove(param);
			if (FParameters[address].Count == 0)
				FParameters.Remove(address);
		}
		
		private IParameter GetParameter<T>(byte[] id, int sliceCount, int dimensions, ITypeDefinition typeDefinition, IPin2 pin, Func<IPin2, int, T> parse) where T: struct
		{
			if (sliceCount > 1)
			{
				var adef = new ArrayDefinition<T>(typeDefinition, (uint)sliceCount);
				var param = (ArrayParameter<T>)ParameterFactory.CreateArrayParameter<T>(id, adef); 
				var values = new List<T>();
				for (int i=0; i<sliceCount; i+=dimensions)
					values.Add(parse(pin, i));
				param.Value = values; 
				return param;
			}
			else
			{
				var param = (ValueParameter<T>)ParameterFactory.CreateParameter(id, typeDefinition);
				param.Value = parse(pin, 0);
				return param;
			}
		}
		
		private IParameter GetNumberParameter<T>(byte[] id, int sliceCount, int dimensions, ITypeDefinition typeDefinition, IPin2 pin, Func<IPin2, int, T> parse) where T: struct
		{
			if (sliceCount > 1)
			{
				var adef = new ArrayDefinition<T>(typeDefinition, (uint)sliceCount);
				var param = (ArrayParameter<T>)ParameterFactory.CreateArrayParameter<T>(id, adef); 
				var values = new List<T>();
				for (int i=0; i<sliceCount; i++)
				{
					//FLogger.Log(LogType.Debug, pin[i*dimensions]);
					values.Add(parse(pin, i*dimensions));
				}
					
				param.Value = values; 
				
				return param;
			}
			else
			{
				var param = (NumberParameter<T>)ParameterFactory.CreateParameter(id, typeDefinition);
				param.Value = parse(pin, 0);
				return param;
			}
		}
		
		private void LabelChanged(object sender, EventArgs e)
		{
			var labelPin = sender as IPin2;
			var id = IdFromPin(labelPin);
			
			var param = FRCPServer.GetParameter(id.ToRCPId());
			param.Label = labelPin.Spread.Trim('|');
			FRCPServer.UpdateParameter(param);
		}
		
		private void TagChanged(object sender, EventArgs e)
		{
			var tagPin = sender as IPin2;
			var id = IdFromPin(tagPin);
			
			var param = FRCPServer.GetParameter(id.ToRCPId());
			param.Userdata = Encoding.UTF8.GetBytes(tagPin.Spread.Trim('|'));
			FRCPServer.UpdateParameter(param);
		}
		
		//the application updated a value
		private void PinValueChanged(object sender, EventArgs e)
		{
			//here it coult make sense to think about a
			//beginframe/endframe bracket to not send every changed pin directly
			//but collect them and send them per frame in a bundle
			var pin = sender as IPin2;
			var id = IdFromPin(pin);
			
			var param = FRCPServer.GetParameter(id.ToRCPId());
			//in case of enum pin we also update the full definition here
			//which may have changed in the meantime
			//TODO: subscribe to enum-changes on the host and update all related
			//parameters as changes happen, so a client can update its gui accordingly
			if (pin.Type == "Enumeration")
			{
				var subtype = pin.SubType.Split(',').Select(s => s.Trim()).ToArray();
				var enumName = subtype[1].Trim();
				var dflt = subtype[2].Trim();
				var newDef = GetEnumDefinition(enumName, dflt);
				var paramDef = param.TypeDefinition as EnumDefinition;
				paramDef.Default = newDef.Default;
				paramDef.Entries = newDef.Entries;
				//FLogger.Log(LogType.Debug, "count: " + pin.Spread);
			}
			param = RCP.Helpers.StringToValue(param, pin.Spread);
			
			FRCPServer.UpdateParameter(param);
			
			//OutputBytes(param);
		}
		
		private void OutputBytes(IParameter param)
		{
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				param.Write(writer);
				//FOutput.AssignFrom(stream.ToArray());
			}
		}
		
		//an RCP client has updated a value
		private void ParameterUpdated(IParameter parameter)
		{
			lock(FParameterQueue)
			FParameterQueue.Add(parameter);
		}
	}
}

namespace RCP
{
	public static class Helpers
	{
		//vvvv string/enum escaping rules:
		//if a slice contains either a space " ", a pipe "|" or a comma ","
		//the slice is quoted with pipes "|like so|"
		//and also every pipe is escaped with another pipe "|like||so|" to encode a string like "like|so"
		
		private static string PipeEscape(string input)
		{
			if (input.Contains(",") || input.Contains("|") || input.Contains(" "))
			{
				input = input.Replace("|", "||");
				input = "|" + input + "|";
			}
			return input;
		}
		
		public static string ValueToString(dynamic param)
		{
			try
			{
				switch ((RcpTypes.Datatype)param.TypeDefinition.Datatype)
				{
					case RcpTypes.Datatype.Boolean: return RCP.Helpers.BoolToString(param.Value);
					case RcpTypes.Datatype.String: return PipeEscape(param.Value.ToString());
					case RcpTypes.Datatype.Enum: return PipeEscape(RCP.Helpers.EnumToString(param.Value, ((IEnumDefinition)param.TypeDefinition).Entries));
					case RcpTypes.Datatype.Float32: return RCP.Helpers.Float32ToString(param.Value);
					case RcpTypes.Datatype.Vector2f32: return RCP.Helpers.Vector2f32ToString(param.Value);
					case RcpTypes.Datatype.Vector3f32: return RCP.Helpers.Vector3f32ToString(param.Value);
					case RcpTypes.Datatype.Rgba: return RCP.Helpers.ColorToString(param.Value);
					case RcpTypes.Datatype.FixedArray:
					{
						switch ((RcpTypes.Datatype)param.TypeDefinition.Subtype.Datatype)
						{
							case RcpTypes.Datatype.Boolean:
							{
								var val = ((ArrayParameter<bool>)param).Value;
								return string.Join(",", val.Select(v => BoolToString(v)));
							}
							case RcpTypes.Datatype.Enum:
							{
								//TODO; accessing the subtypes entries fails
								var val = ((ArrayParameter<ushort>)param).Value;
								return string.Join(",", val.Select(v => PipeEscape(EnumToString(v, ((IEnumDefinition)((ArrayDefinition<ushort>)param.TypeDefinition).Subtype).Entries))));
							}						
							case RcpTypes.Datatype.Int32:
							{
								var val = ((ArrayParameter<int>)param).Value;
								return string.Join(",", val.Select(v => Int32ToString(v)));
							}
							case RcpTypes.Datatype.Float32:
							{
								var val = ((ArrayParameter<float>)param).Value;
								return string.Join(",", val.Select(v => Float32ToString(v)));
							}
							case RcpTypes.Datatype.Vector2f32:
							{
								var val = ((ArrayParameter<V2>)param).Value;
								return string.Join(",", val.Select(v => Vector2f32ToString(v)));
							}						
							case RcpTypes.Datatype.String:
							{
								var val = ((ArrayParameter<string>)param).Value;
								return string.Join(",", val.Select(v => PipeEscape(v)));
							}
							case RcpTypes.Datatype.Rgba:
							{
								var val = ((ArrayParameter<Color>)param).Value;
								return string.Join(",", val.Select(v => ColorToString(v)));
							}
							
							default: return param.Value.ToString();
						}
					}
					default: return param.Value.ToString();
				}
			}
			catch (Exception e)
			{
				return e.Message;
			}
		}
		
		public static string PipeUnEscape(string input)
		{
			if (input[0] == '|' && input[input.Length-1] == '|')
				input = input.Substring(1, input.Length-2);
			return input.Replace("||", "|");
		}
		
		//sets the value given as string on the given parameter
		public static IParameter StringToValue(dynamic param, string input)
		{
			try
			{
				switch((RcpTypes.Datatype)param.TypeDefinition.Datatype)
				{
					case RcpTypes.Datatype.Boolean:
					{
						var p = (BooleanParameter)param;
						p.Value = ParseBool(input);
						return p;
					}
					
					case RcpTypes.Datatype.Enum:
					{
						var p = (EnumParameter)param;
						p.Value = ParseEnum(PipeUnEscape(input), ((IEnumDefinition)param.TypeDefinition).Entries);
						return p;
					}
					
					case RcpTypes.Datatype.Int32:
					{
						var p = (NumberParameter<int>)param;
						p.Value = ParseInt(input);
						return p;
					}
					
					case RcpTypes.Datatype.Float32:
					{
						var p = (NumberParameter<float>)param;
						p.Value = ParseFloat(input);
						return p;
					}
					
					case RcpTypes.Datatype.String:
					{
						var p = (StringParameter)param;
						p.Value = PipeUnEscape(input);
						return p;
					}
					
					case RcpTypes.Datatype.Rgba:
					{
						var p = (RGBAParameter)param;
						p.Value = ParseColor(input);
						return p;
					}
					
					case RcpTypes.Datatype.Vector2f32:
					{
						var p = (NumberParameter<V2>)param;
						p.Value = ParseVector2(input);
						return p;
					}
					
					case RcpTypes.Datatype.Vector3f32:
					{
						var p = (NumberParameter<V3>)param;
						p.Value = ParseVector3(input);
						return p;
					}
					
					case RcpTypes.Datatype.FixedArray:
					{
						//TODO; handle array types properly
						switch ((RcpTypes.Datatype)param.TypeDefinition.Subtype.Datatype)
						{
							case RcpTypes.Datatype.Boolean:
							{
								var p = (ArrayParameter<bool>)param;
								p.Value = input.Split(',').Select(s => ParseBool(s)).ToList();
								return p;
							}
							
							case RcpTypes.Datatype.Enum:
							{
								var p = (ArrayParameter<ushort>)param;
								p.Value = SplitToSlices(input).Select(s => ParseEnum(PipeUnEscape(s), ((IEnumDefinition)param.TypeDefinition).Entries)).ToList();
								return p;
							}
							
							case RcpTypes.Datatype.Int32:
							{
								var p = (ArrayParameter<int>)param;
								p.Value = input.Split(',').Select(s => ParseInt(s)).ToList();
								return p;
							}
							
							case RcpTypes.Datatype.String:
							{
								var p = (ArrayParameter<string>)param;
								p.Value = SplitToSlices(input).Select(s => PipeUnEscape(s)).ToList();
								return p;
							}
							
							case RcpTypes.Datatype.Float32:
							{
								var p = (ArrayParameter<float>)param;
								p.Value = input.Split(',').Select(s => ParseFloat(s)).ToList();
								return p;
							}
							
							case RcpTypes.Datatype.Vector2f32:
							{
								var p = (ArrayParameter<V2>)param;
								var v = input.Split(',');
								p.Value.Clear();
								for (int i=0; i<v.Count()/2; i++)
									p.Value.Add(new V2(ParseFloat(v[i*2]), ParseFloat(v[i*2+1])));
								return p;
							}
							
							case RcpTypes.Datatype.Vector3f32:
							{
								var p = (ArrayParameter<V3>)param;
								var v = input.Split(',');
								p.Value.Clear();
								for (int i=0; i<v.Count()/3; i++)
									p.Value.Add(new V3(ParseFloat(v[i*3]), ParseFloat(v[i*3+1]), ParseFloat(v[i*3+2])));
								return p;
							}
							
							case RcpTypes.Datatype.Rgba:
							{
								var p = (ArrayParameter<Color>)param;
								//split at commas outside of pipes
								p.Value = SplitToSlices(input).Select(s => ParseColor(s)).ToList();
								return p;
							}
						}
						break;
					}
				}
			}
			catch
			{
				//string parsing went wrong...						
			}
			
			return param;
		}
		
		private static List<string> SplitToSlices(string input)
		{
			return Regex.Split(input, @",(?=(?:[^\|]*\|[^\|]*\|)*[^\|]*$)").ToList();
		}
		
		public static V2 GetVector2(IPin2 pin, int index)
		{
			var x = ParseFloat(pin[index]);
			var y = ParseFloat(pin[index+1]);
			return new V2(x, y);
		}
		
		public static V2 ParseVector2(string input)
		{
			var comps = input.Split(',');
			return new V2(ParseFloat(comps[0]), ParseFloat(comps[1]));
		}
		
		public static V3 GetVector3(IPin2 pin, int index)
		{
			var x = ParseFloat(pin[index]);
			var y = ParseFloat(pin[index+1]);
			var z = ParseFloat(pin[index+2]);
			return new V3(x, y, z);
		}
		
		public static V3 ParseVector3(string input)
		{
			var comps = input.Split(',');
			return new V3(ParseFloat(comps[0]), ParseFloat(comps[1]), ParseFloat(comps[2]));
		}
		
		public static bool ParseBool(string input)
		{
			return input == "1" ? true : false;
		}
		
		public static ushort ParseEnum(string input, string[] entries)
		{
			return (ushort)entries.ToList().IndexOf(input);
		}
		
		public static float GetFloat(IPin2 pin, int index)
		{
			return ParseFloat(pin[index]);
		}
		
		public static float ParseFloat(string input)
		{
			float v;
			float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
			return v;
		}
		
		public static int GetInt(IPin2 pin, int index)
		{
			return ParseInt(pin[index]);
		}
		
		public static int ParseInt(string input)
		{
			int v;
			int.TryParse(input, out v);
			return v;
		}
		
		public static Color GetColor(IPin2 pin, int index)
		{
			return ParseColor(pin[index]);
		}
		
		public static Color ParseColor(string input)
		{
			var comps = input.Trim('|').Split(',');
	        var r = 255 * float.Parse(comps[0], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var g = 255 * float.Parse(comps[1], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var b = 255 * float.Parse(comps[2], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var a = 255 * float.Parse(comps[3], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var color = Color.FromArgb((int)a, (int)r, (int)g, (int)b);
			return color;
		}
		
		public static string ColorToString(Color input)
		{
			return "|" + Float32ToString(input.R / 255f) + "," + Float32ToString(input.G / 255f) + "," + Float32ToString(input.B / 255f) + "," + Float32ToString(input.A / 255f) + "|";
		}
		
		public static string BoolToString(bool input)
		{
			return input ? "1" : "0";
		}
		
		public static string Float32ToString(float input)
		{
			return input.ToString(CultureInfo.InvariantCulture);
		}
		
		public static string Int32ToString(int input)
		{
			return input.ToString(CultureInfo.InvariantCulture);
		}
		
		public static string EnumToString(ushort input, string[] entries)
		{
			if (input >= 0 && input < entries.Length)
				return entries[input];
			else
				return "";
		}
		
		public static string Vector2f32ToString(V2 input)
		{
			return Float32ToString(input.X) + "," + Float32ToString(input.Y);
		}
		
		public static string Vector3f32ToString(V3 input)
		{
			return Float32ToString(input.X) + "," + Float32ToString(input.Y) + "," + Float32ToString(input.Z);
		}
		
		//TODO:
		public static string TypeDefinitionToString(ITypeDefinition definition)
		{
			try
			{
				switch(definition.Datatype)
				{
					case RcpTypes.Datatype.Boolean:
					{
						var def = (IBooleanDefinition)definition;
						return def.Default ? "1" : "0";
					}
					
					case RcpTypes.Datatype.Enum:
					{
						var def = (IEnumDefinition)definition;
						return EnumToString(def.Default, ((IEnumDefinition)def).Entries) + ", [" + string.Join(",", def.Entries) + "]";
					}
					
					case RcpTypes.Datatype.Int32:
					{
						var def = (INumberDefinition<int>)definition;
						return Int32ToString(def.Default) + ", " + Int32ToString((int)def.Minimum) + ", " + Int32ToString((int)def.Maximum) + ", " + Int32ToString((int)def.MultipleOf);
					}
					
					case RcpTypes.Datatype.Float32:
					{
						var def = (INumberDefinition<float>)definition;
						return Float32ToString(def.Default) + ", " + Float32ToString((float)def.Minimum) + ", " + Float32ToString((float)def.Maximum) + ", " + Float32ToString((float)def.MultipleOf);
					}
					
					case RcpTypes.Datatype.Vector2f32:
					{
						var def = (INumberDefinition<V2>)definition;
						return Vector2f32ToString(def.Default) + ", " + Vector2f32ToString((V2)def.Minimum) + ", " + Vector2f32ToString((V2)def.Maximum) + ", " + Vector2f32ToString((V2)def.MultipleOf);
					}
					
					case RcpTypes.Datatype.Vector3f32:
					{
						var def = (INumberDefinition<V3>)definition;
						return Vector3f32ToString(def.Default) + ", " + Vector3f32ToString((V3)def.Minimum) + ", " + Vector3f32ToString((V3)def.Maximum) + ", " + Vector3f32ToString((V3)def.MultipleOf);
					}
					
					case RcpTypes.Datatype.String:
					{
						var def = (IStringDefinition)definition;
						return def.Default;
					}
					
					case RcpTypes.Datatype.Uri:
					{
						var def = (IUriDefinition)definition;
						return def.Default + ", " + def.Schema + ", " + def.Filter;
					}
					
					case RcpTypes.Datatype.Rgba:
					{
						var def = (IRGBADefinition)definition;
						return ColorToString(def.Default);
					}
					
					case RcpTypes.Datatype.FixedArray:
					{
						dynamic def = definition;
						switch((RcpTypes.Datatype)def.Subtype.Datatype)
						{
							case RcpTypes.Datatype.Boolean: return TypeDefinitionToString((IBooleanDefinition)def.Subtype);
							case RcpTypes.Datatype.Float32: return TypeDefinitionToString((INumberDefinition<float>)def.Subtype);
							
							default: return "Unknown Type";
						}
					}
					
					default: return "Unknown Type";
				}
			}
			catch (Exception e)
			{
				return e.Message;
			}
		}
	}
}