#region usings
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Linq;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;
using VVVV.Core.Logging;

using RCP;
using RCP.Model;
using RCP.Parameter;
using RCP.Transporter;

using Kaitai;
#endregion usings

namespace VVVV.Nodes
{
	public class Parameter
	{
		byte[] FId, FParent;
		string FDatatype, FTypeDefinition, FValue, FLabel, FUserdata;
		
		public Parameter ()
		{}
		
		public Parameter (byte[] id, string datatype, string typeDefinition, string value, string label, byte[] parent, string userdata)
		{
			FId = id;
			FDatatype = datatype;
			FTypeDefinition = typeDefinition;
			FValue = value;
			FLabel = label;
			FParent = parent;
			FUserdata = userdata;
		}

		public byte[] Id => FId;
		public string Datatype => FDatatype;
		public string TypeDefinition => FTypeDefinition;
		public string Value => FValue;
		public string Label => FLabel;
		public byte[] Parent => FParent;	
		public string Userdata => FUserdata;
	}
	
	#region PluginInfo
	[PluginInfo(Name = "ParameterByGroup", 
				Category = "RCP", 
				Version = "",
				Help = "Filter parameters by group")]
	#endregion PluginInfo
	public class RCPParameterByGroupNode : IPluginEvaluate
	{ 
		#region fields & pins
		[Input("Input")]
		public ISpread<Parameter> FParameters;
		
		[Input("Group")]
		public ISpread<string> FGroup;
		
		[Output("Output")]
		public ISpread<Parameter> FParametersOut;
		#endregion

		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			if (string.IsNullOrWhiteSpace(FGroup[0]))
				FParametersOut.AssignFrom(FParameters);
			else
			{
				var groups = FGroup.ToList();
				FParametersOut.AssignFrom(FParameters.Where(p => groups.Contains(p.Parent.ToIdString())));
			}
		}
	}
	
	#region PluginInfo
	[PluginInfo(Name = "Parameter", 
				Category = "RCP", 
				Version = "Split",
				Help = "An RCP Parameter")]
	#endregion PluginInfo
	public class RCPParameterSplitNode : IPluginEvaluate
	{ 
		#region fields & pins
		[Input("Input")]
		public ISpread<Parameter> FParameter;
		
		[Output("ID")]
		public ISpread<string> FId;
		
		[Output("Datatype")]
		public ISpread<string> FDatatype;
		
		[Output("Type Definition")]
		public ISpread<string> FTypeDefinition;
		
		[Output("Value")]
		public ISpread<string> FValue;
		
		[Output("Label")]
		public ISpread<string> FLabel;
		
//		[Output("Order")]
//		public ISpread<int> FOrder;
		
//		[Output("Parent")]
//		public ISpread<string> FParent;
		
//		[Output("Widget")]
//		public ISpread<string> FWidget;
		
		[Output("Userdata")]
		public ISpread<string> FUserdata;
		#endregion fields & pins
		
		public RCPParameterSplitNode()
		{ }
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			try
			{
				FId.AssignFrom(FParameter.Select(p => p.Id.ToIdString()));
				FDatatype.AssignFrom(FParameter.Select(p => p.Datatype));
				FTypeDefinition.AssignFrom(FParameter.Select(p => p.TypeDefinition));
				FValue.AssignFrom(FParameter.Select(p => p.Value));
				FLabel.AssignFrom(FParameter.Select(p => p.Label));
		//			FParent.AssignFrom(FParameter.Select(p => p.Parent?.ToIdString() ?? ""));
				FUserdata.AssignFrom(FParameter.Select(p => p.Userdata));
			}
			catch {}
		}
	}
	
	#region PluginInfo
	[PluginInfo(Name = "Parameter", 
				Category = "RCP", 
				Version = "Join",
				Help = "An RCP Parameter")]
	#endregion PluginInfo
	public class RCPParameterJoinNode : IPluginEvaluate
	{
		#region fields & pins
		[Input("ID")]
		public ISpread<string> FId;
		
		[Input("Datatype")]
		public ISpread<string> FDatatype;
		
		[Input("Type Definition")]
		public ISpread<string> FTypeDefinition;
		
		[Input("Value")]
		public ISpread<string> FValue;
		
		[Input("Label")]
		public ISpread<string> FLabel;
		
//		[Output("Order")]
//		public ISpread<int> FOrder;
		
//		[Input("Parent")]
//		public ISpread<string> FParent;
		
//		[Output("Widget")]
//		public ISpread<string> FWidget;
		
		[Input("Userdata")]
		public ISpread<string> FUserdata;
		
		[Output("Output")]
		public ISpread<Parameter> FParameter;
		
		List<Parameter> FParams = new List<Parameter>();
		
		#endregion fields & pins
		
		public RCPParameterJoinNode()
		{ }
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			FParams.Clear();
			for (int i=0; i<SpreadMax; i++)
			{
				var param = new Parameter(FId[i].ToRCPId(), FDatatype[i], FTypeDefinition[i], FValue[i], FLabel[i], new byte[0]{}/*FParent[i].ToRCPId()*/, FUserdata[i]);
				FParams.Add(param);
			}	
			
			FParameter.AssignFrom(FParams);
		}
	}
}