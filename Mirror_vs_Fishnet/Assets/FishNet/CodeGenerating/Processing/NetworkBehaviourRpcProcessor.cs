﻿
using FishNet.CodeGenerating.Helping;
using FishNet.CodeGenerating.Helping.Extension;
using FishNet.Connection;
using FishNet.Managing.Logging;
using FishNet.Object.Helping;
using FishNet.Transporting;
using MonoFN.Cecil;
using MonoFN.Cecil.Cil;
using MonoFN.Cecil.Rocks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using SR = System.Reflection;

namespace FishNet.CodeGenerating.Processing
{
    internal class NetworkBehaviourRpcProcessor
    {

        #region Types.
        private struct DelegateData
        {
            public RpcType RpcType;
            public bool RunLocally;
            public MethodDefinition OriginalMethodDef;
            public MethodDefinition ReaderMethodDef;
            public uint MethodHash;
            public CustomAttribute RpcAttribute;

            public DelegateData(RpcType rpcType, bool runLocally, MethodDefinition originalMethodDef, MethodDefinition readerMethodDef, uint methodHash, CustomAttribute rpcAttribute)
            {
                RpcType = rpcType;
                RunLocally = runLocally;
                OriginalMethodDef = originalMethodDef;
                ReaderMethodDef = readerMethodDef;
                MethodHash = methodHash;
                RpcAttribute = rpcAttribute;
            }
        }

        #endregion

        private List<(MethodDefinition, MethodDefinition)> _virtualRpcs = new List<(MethodDefinition createdLogicMd, MethodDefinition originalRpcMd)>();

        #region Const.
        private const string LOGIC_PREFIX = "RpcLogic___";
        private const string WRITER_PREFIX = "RpcWriter___";
        private const string READER_PREFIX = "RpcReader___";
        private const string REQUIREOWNERSHIP_NAME = "RequireOwnership";
        private const string RUNLOCALLY_NAME = "RunLocally";
        private const string INCLUDEOWNER_NAME = "IncludeOwner";
        private const string BUFFERLAST_NAME = "BufferLast";
        #endregion

        internal bool Process(TypeDefinition typeDef, ref uint rpcCount)
        {
            bool modified = false;

            List<DelegateData> delegateDatas = new List<DelegateData>();
            List<MethodDefinition> methodDefs = typeDef.Methods.ToList();
            foreach (MethodDefinition md in methodDefs)
            {
                if (rpcCount >= ObjectHelper.MAX_RPC_ALLOWANCE)
                {
                    CodegenSession.LogError($"{typeDef.FullName} and inherited types exceed {ObjectHelper.MAX_RPC_ALLOWANCE} RPC methods. Only {ObjectHelper.MAX_RPC_ALLOWANCE} RPC methods are supported per inheritance hierarchy.");
                    return false;
                }

                RpcType rpcType;
                //CHANGE THIS TO GetRpcAttributes
                /* Make observersRpc ignore conn parameter if also
                 * target rpc. generate reader/writers for each method
                 * normally. check rpcType on each returned result.
                 * process each returned result. 
                 *
                 * Figure out a way to tell CreateRpcMethods that an observer rpc may
                 * also be a target rpc.
                 */
                CustomAttribute rpcAttribute = GetRpcAttribute(md, true, out rpcType);
                if (rpcAttribute == null)
                    continue;

                /* This is a one time check to make sure the rpcType is
                 * a supported value. Multiple methods beyond this rely on the
                 * value being supported. Rather than check in each method a
                 * single check is performed here. */
                if (rpcType != RpcType.Observers && rpcType != RpcType.Server && rpcType != RpcType.Target)
                {
                    CodegenSession.LogError($"RpcType of {rpcType.ToString()} is unhandled.");
                    continue;
                }

                //Create methods for users method.
                MethodDefinition writerMethodDef, readerMethodDef, logicMethodDef;
                bool runLocally;
                bool createResult = CreateRpcMethods(typeDef, md, rpcAttribute, rpcType, rpcCount, out writerMethodDef, out readerMethodDef, out logicMethodDef, out runLocally);

                if (createResult)
                {
                    modified = true;

                    delegateDatas.Add(new DelegateData(rpcType, runLocally, md, readerMethodDef, rpcCount, rpcAttribute));
                    if (logicMethodDef != null && logicMethodDef.IsVirtual)
                        _virtualRpcs.Add((logicMethodDef, md));

                    rpcCount++;
                }
            }

            if (modified)
            {
                foreach (DelegateData data in delegateDatas)
                    CodegenSession.ObjectHelper.CreateRpcDelegate(data.RunLocally, typeDef,
                        data.ReaderMethodDef, data.RpcType, data.MethodHash,
                        data.RpcAttribute);

                modified = true;
            }

            return modified;
        }


        /// <summary>
        /// Returns the method name with parameter types included within the name.
        /// </summary>
        private string GetMethodNameAsParameters(MethodDefinition methodDef)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(methodDef.Name);
            foreach (ParameterDefinition pd in methodDef.Parameters)
                sb.Append(pd.ParameterType.FullName);

            return sb.ToString();
        }

        /// <summary>
        /// Redirects base calls for overriden RPCs.
        /// </summary>
        internal void RedirectBaseCalls()
        {
            foreach ((MethodDefinition logicMd, MethodDefinition originalMd) in _virtualRpcs)
                RedirectBaseCall(logicMd, originalMd);
        }

        /// <summary>
        /// Gets number of RPCs by checking for RPC attributes. This does not perform error checking.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        internal uint GetRpcCount(TypeDefinition typeDef)
        {
            uint count = 0;
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                foreach (CustomAttribute customAttribute in methodDef.CustomAttributes)
                {
                    RpcType rpcType = CodegenSession.AttributeHelper.GetRpcAttributeType(customAttribute);
                    if (rpcType != RpcType.None)
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Gets all Rpc attributes on a method.
        /// </summary>
        /// <returns></returns>
        private List<(RpcType, CustomAttribute)> GetRpcAttributes(MethodDefinition methodDef, bool validate)
        {
            List<(RpcType, CustomAttribute)> results = new List<(RpcType, CustomAttribute)>();

            bool hasTargetRpc = false;
            bool hasObserversRpc = false;

            foreach (CustomAttribute customAttribute in methodDef.CustomAttributes)
            {
                RpcType rpcType = CodegenSession.AttributeHelper.GetRpcAttributeType(customAttribute);
                if (rpcType != RpcType.None)
                {
                    results.Add((rpcType, customAttribute));
                    if (validate)
                    {
                        if (rpcType == RpcType.Target)
                            hasTargetRpc = true;
                        else if (rpcType == RpcType.Observers)
                            hasObserversRpc = true;
                    }
                }
            }

            if (validate)
            {
                bool invalidRpcCount = false;
                //More than 2 are never allowed.
                if (results.Count > 2)
                    invalidRpcCount = true;
                //If 2 then make sure they are target and observer.
                else if (results.Count == 2)
                    invalidRpcCount = (!hasTargetRpc || !hasObserversRpc);

                if (invalidRpcCount)
                {
                    CodegenSession.LogError($"{methodDef.Name} RPC method has an unsupported Rpc attribute combination. Only ObserversRpc and TargetRpc may be used together.");
                }

                //Check each attribute.
                foreach ((RpcType RpcType, CustomAttribute CustomAttribute) item in results)
                {
                    if (!IsRpcMethodValid(methodDef, item.RpcType))
                        return null;
                }
            }

            //Fall through, success.
            return results;
        }

        /// <summary>
        /// Returns the RPC attribute on a method, if one exist. Otherwise returns null.
        /// </summary>
        /// <param name="validate">True to validate parameters and check for serializers.</param>
        /// <returns></returns>
        internal CustomAttribute GetRpcAttribute(MethodDefinition methodDef, bool validate, out RpcType rpcType)
        {
            //True if an error occurred.
            bool error = false;
            //Last rpc attribute found.
            CustomAttribute rpcAttribute = null;
            //Found RpcType.
            rpcType = RpcType.None;
            //Number of rpc attributes found.
            uint foundAttributes = 0;

            foreach (CustomAttribute customAttribute in methodDef.CustomAttributes)
            {
                RpcType tmpRpcType = CodegenSession.AttributeHelper.GetRpcAttributeType(customAttribute);
                if (tmpRpcType != RpcType.None)
                {
                    rpcType |= tmpRpcType;
                    rpcAttribute = customAttribute;
                    foundAttributes++;
                }
            }

            if (validate && rpcAttribute != null)
            {
                /* A RPC attribute already exist. Only Observers and Target
                 * attributes are allowed to stack, so make sure these are
                 * what are being used. */
                if (foundAttributes > 1)
                {
                    CodegenSession.LogError($"{methodDef.Name} RPC method cannot have multiple RPC attributes.");
                    error = true;
                }

                if (!IsRpcMethodValid(methodDef, rpcType))
                    error = true;
            }

            //If an error occurred then reset results.
            if (error)
            {
                rpcAttribute = null;
                rpcType = RpcType.None;
            }

            return rpcAttribute;
        }

        /// <summary>
        /// Returns if a RpcMethod has valid serialization and setup.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="rpcType"></param>
        /// <returns></returns>
        private bool IsRpcMethodValid(MethodDefinition methodDef, RpcType rpcType)
        {
            //Virtual method.
            //else if (methodDef.Attributes.HasFlag(MethodAttributes.Virtual))
            //{
            //    CodegenSession.LogError($"{methodDef.Name} RPC method cannot be virtual.");
            //    error = true;
            //}
            //Static method.
            if (methodDef.IsStatic)
            {
                CodegenSession.LogError($"{methodDef.Name} RPC method cannot be static.");
                return false;
            }
            //Is generic type.
            else if (methodDef.HasGenericParameters)
            {
                CodegenSession.LogError($"{methodDef.Name} RPC method cannot contain generic parameters.");
                return false;
            }
            //Abstract method.
            else if (methodDef.IsAbstract)
            {
                CodegenSession.LogError($"{methodDef.Name} RPC method cannot be abstract.");
                return false;
            }
            //Non void return.
            else if (methodDef.ReturnType != methodDef.Module.TypeSystem.Void)
            {
                CodegenSession.LogError($"{methodDef.Name} RPC method must return void.");
                return false;
            }
            //Misc failing conditions.
            else
            {
                ////Check if is overloaded.
                //TypeDefinition td = methodDef.DeclaringType;
                //foreach (MethodDefinition md in td.GetMethods())
                //{
                //    if (md != methodDef && md.Name == methodDef.Name)
                //    {
                //        CodegenSession.LogError($"{methodDef.Name} RPC method cannot be overloaded. This feature will be provided in a later release.");
                //        error = true;
                //        break;
                //    }
                //}
            }
            //TargetRpc but missing correct parameters.
            if (rpcType == RpcType.Target)
            {
                if (methodDef.Parameters.Count == 0 || !methodDef.Parameters[0].Is(typeof(NetworkConnection)))
                {
                    CodegenSession.LogError($"Target RPC {methodDef.Name} must have a NetworkConnection as the first parameter.");
                    return false;
                }
            }

            //Make sure all parameters can be serialized.
            for (int i = 0; i < methodDef.Parameters.Count; i++)
            {
                ParameterDefinition parameterDef = methodDef.Parameters[i];

                //If NetworkConnection, TargetRpc, and first parameter.
                if ((i == 0) && (rpcType == RpcType.Target) && parameterDef.Is(typeof(NetworkConnection)))
                    continue;

                if (parameterDef.ParameterType.IsGenericParameter)
                {
                    CodegenSession.LogError($"RPC method{methodDef.Name} contains a generic parameter. This is currently not supported.");
                    return false;
                }

                //Can be serialized/deserialized.
                bool canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(parameterDef.ParameterType, true);
                if (!canSerialize)
                {
                    CodegenSession.LogError($"RPC method {methodDef.Name} parameter type {parameterDef.ParameterType.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                    return false;
                }

            }

            //Fall through, success.
            return true;
        }

        /// <summary>
        /// Creates all methods needed for a RPC.
        /// </summary>
        /// <param name="originalMd"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns>True if successful.</returns>
        private bool CreateRpcMethods(TypeDefinition typeDef, MethodDefinition originalMd, CustomAttribute rpcAttribute, RpcType rpcType, uint allRpcCount,
            out MethodDefinition writerMd, out MethodDefinition readerMd, out MethodDefinition logicMd, out bool runLocally)
        {
            writerMd = null;
            readerMd = null;
            logicMd = null;
            runLocally = rpcAttribute.GetField(RUNLOCALLY_NAME, false);
            bool intentionallyNull;

            List<ParameterDefinition> serializedParameters = GetSerializedParamters(rpcType, originalMd);

            writerMd = CreateRpcWriterMethod(typeDef, originalMd, serializedParameters, rpcAttribute, rpcType, allRpcCount, out intentionallyNull);
            if (!intentionallyNull && writerMd == null)
                return false;

            logicMd = CreateRpcLogicMethod(typeDef, runLocally, originalMd, serializedParameters, rpcType, out intentionallyNull);
            if (!intentionallyNull && logicMd == null)
                return false;

            readerMd = CreateRpcReaderMethod(typeDef, originalMd, serializedParameters, logicMd, rpcAttribute, rpcType, out intentionallyNull);
            if (!intentionallyNull && readerMd == null)
                return false;

            /* If writer is null then the side this build is
             * for does not need it. */
            if (writerMd == null)
            {
                /* Ideally the method would be removed entirely
                 * but somewhere in the codegen are references
                 * to the data types that are neded. Could be in the reader,
                 * or logic method. I'm not sure but that's a rabbit hole
                 * I don't want to go down right now. For now just
                 * erase the method contents. The method signature
                 * alone won't reveal anything. */
                originalMd.ClearMethodWithRet();
            }
            else
            {
                RedirectRpcMethod(originalMd, writerMd, logicMd, runLocally);
            }

            return true;
        }



        /// <summary>
        /// Creates a writer for a RPC.
        /// </summary>
        /// <param name="originalMethodDef"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns></returns>
        private MethodDefinition CreateRpcWriterMethod(TypeDefinition typeDef, MethodDefinition originalMethodDef, List<ParameterDefinition> serializedParameters, CustomAttribute rpcAttribute, RpcType rpcType, uint allRpcCount, out bool intentionallyNull)
        {
            intentionallyNull = false;

            

            string methodName = $"{WRITER_PREFIX}{GetMethodNameAsParameters(originalMethodDef)}";
            /* If method already exist then clear it. This
             * can occur when a method needs to be rebuilt due to
             * inheritence, and renumbering the RPC method names. */
            MethodDefinition createdMethodDef = typeDef.GetMethod(methodName);
            //If found.
            if (createdMethodDef != null)
            {
                createdMethodDef.Parameters.Clear();
                createdMethodDef.Body.Instructions.Clear();
            }
            //Doesn't exist, create it.
            else
            {
                //Create the method body.
                createdMethodDef = new MethodDefinition(methodName,
                    MethodAttributes.Private,
                    originalMethodDef.Module.TypeSystem.Void);
                typeDef.Methods.Add(createdMethodDef);
                createdMethodDef.Body.InitLocals = true;
            }

            if (rpcType == RpcType.Server)
                return CreateServerRpcWriterMethod(typeDef, originalMethodDef, createdMethodDef, rpcAttribute, allRpcCount, serializedParameters);
            else if (rpcType == RpcType.Target || rpcType == RpcType.Observers)
                return CreateClientRpcWriterMethod(typeDef, originalMethodDef, createdMethodDef, rpcAttribute, allRpcCount, serializedParameters, rpcType);
            else
                return null;
        }

        /// <summary>
        /// Returns serializable parameters for originalMd.
        /// </summary>
        private List<ParameterDefinition> GetSerializedParamters(RpcType rpcType, MethodDefinition originalMd)
        {
            List<ParameterDefinition> serializedParameters = new List<ParameterDefinition>();

            //Get channel if it exist, and get target parameter.
            ParameterDefinition channelParameterDef = GetChannelParameter(originalMd, rpcType);

            /* RpcType specific parameters. */
            ParameterDefinition targetConnectionParameterDef = null;
            if (rpcType == RpcType.Target)
                targetConnectionParameterDef = originalMd.Parameters[0];

            /* Parameters which won't be serialized, such as channel.
             * It's safe to add parameters which are null or
             * not used. */
            HashSet<ParameterDefinition> nonserializedParameters = new HashSet<ParameterDefinition>();

            if (rpcType == RpcType.Server)
            {
                //The network connection parameter might be added as null, this is okay.
                nonserializedParameters.Add(GetNetworkConnectionParameter(originalMd));
                nonserializedParameters.Add(channelParameterDef);
            }
            else
            {
                nonserializedParameters.Add(channelParameterDef);
                nonserializedParameters.Add(targetConnectionParameterDef);
            }

            //Add all parameters which are NOT nonserialized to serializedParameters.
            foreach (ParameterDefinition pd in originalMd.Parameters)
            {
                if (!nonserializedParameters.Contains(pd))
                    serializedParameters.Add(pd);
            }


            return serializedParameters;
        }

        /// <summary>
        /// Creates Writer method for a TargetRpc.
        /// </summary>
        private MethodDefinition CreateClientRpcWriterMethod(TypeDefinition typeDef, MethodDefinition originalMd, MethodDefinition createdMd, CustomAttribute rpcAttribute, uint allRpcCount, List<ParameterDefinition> serializedParameters, RpcType rpcType)
        {
            ILProcessor createdProcessor = createdMd.Body.GetILProcessor();
            //Add all parameters from the original.
            for (int i = 0; i < originalMd.Parameters.Count; i++)
                createdMd.Parameters.Add(originalMd.Parameters[i]);
            //Get channel if it exist, and get target parameter.
            ParameterDefinition channelParameterDef = GetChannelParameter(createdMd, rpcType);

            /* RpcType specific parameters. */
            ParameterDefinition targetConnectionParameterDef = null;
            if (rpcType == RpcType.Target)
                targetConnectionParameterDef = createdMd.Parameters[0];

            /* Creates basic ServerRpc and ClientRpc
             * conditions such as if requireOwnership ect..
             * or if (!base.isClient) */
            if (!BuildInformation.IsBuilding)
                CreateClientRpcConditionsForServer(createdMd);

            VariableDefinition channelVariableDef = CreateAndPopulateChannelVariable(createdMd, channelParameterDef);
            //Create a local PooledWriter variable.
            VariableDefinition pooledWriterVariableDef = CodegenSession.WriterHelper.CreatePooledWriter(createdMd);
            //Create all writer.WriteType() calls. 
            for (int i = 0; i < serializedParameters.Count; i++)
            {
                MethodReference writeMethodRef = CodegenSession.WriterHelper.GetOrCreateFavoredWriteMethodReference(serializedParameters[i].ParameterType, true);
                if (writeMethodRef == null)
                    return null;

                CodegenSession.WriterHelper.CreateWrite(createdMd, pooledWriterVariableDef, serializedParameters[i], writeMethodRef);
            }

            uint methodHash = allRpcCount;
            //uint methodHash = originalMethodDef.FullName.GetStableHash32();
            /* Call the method on NetworkBehaviour responsible for sending out the rpc. */
            if (rpcType == RpcType.Observers)
                CreateSendObserversRpc(createdMd, methodHash, pooledWriterVariableDef, channelVariableDef, rpcAttribute);
            else if (rpcType == RpcType.Target)
                CreateSendTargetRpc(createdMd, methodHash, pooledWriterVariableDef, channelVariableDef, targetConnectionParameterDef);

            //Dispose of writer.
            createdProcessor.Add(CodegenSession.WriterHelper.DisposePooledWriter(createdMd, pooledWriterVariableDef));
            //Add end of method.
            createdProcessor.Emit(OpCodes.Ret);

            return createdMd;
        }

        /// <summary>
        /// Creates Writer method for a ServerRpc.
        /// </summary>
        private MethodDefinition CreateServerRpcWriterMethod(TypeDefinition typeDef, MethodDefinition originalMd, MethodDefinition createdMd, CustomAttribute rpcAttribute, uint allRpcCount, List<ParameterDefinition> serializedParameters)
        {
            ILProcessor createdProcessor = createdMd.Body.GetILProcessor();
            //Add all parameters from the original.
            for (int i = 0; i < originalMd.Parameters.Count; i++)
                createdMd.Parameters.Add(originalMd.Parameters[i]);
            //Add in channel if it doesnt exist.
            ParameterDefinition channelParameterDef = GetChannelParameter(createdMd, RpcType.Server);

            /* Creates basic ServerRpc
             * conditions such as if requireOwnership ect..
             * or if (!base.isClient) */
            if (!BuildInformation.IsBuilding)
                CreateServerRpcConditionsForClient(createdMd, rpcAttribute);

            VariableDefinition channelVariableDef = CreateAndPopulateChannelVariable(createdMd, channelParameterDef);
            //Create a local PooledWriter variable.
            VariableDefinition pooledWriterVariableDef = CodegenSession.WriterHelper.CreatePooledWriter(createdMd);
            //Create all writer.WriteType() calls. 
            for (int i = 0; i < serializedParameters.Count; i++)
            {
                MethodReference writeMethodRef = CodegenSession.WriterHelper.GetOrCreateFavoredWriteMethodReference(serializedParameters[i].ParameterType, true);
                if (writeMethodRef == null)
                    return null;

                CodegenSession.WriterHelper.CreateWrite(createdMd, pooledWriterVariableDef, serializedParameters[i], writeMethodRef);
            }

            uint methodHash = allRpcCount;
            //uint methodHash = originalMethodDef.FullName.GetStableHash32();
            //Call the method on NetworkBehaviour responsible for sending out the rpc.
            CreateSendServerRpc(createdMd, methodHash, pooledWriterVariableDef, channelVariableDef);
            //Dispose of writer.
            createdProcessor.Add(CodegenSession.WriterHelper.DisposePooledWriter(createdMd, pooledWriterVariableDef));

            //Add end of method.
            createdProcessor.Emit(OpCodes.Ret);

            return createdMd;
        }

        /// <summary>
        /// Creates a Channel VariableDefinition and populates it with parameterDef value if available, otherwise uses Channel.Reliable.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="parameterDef"></param>
        /// <returns></returns>
        private VariableDefinition CreateAndPopulateChannelVariable(MethodDefinition methodDef, ParameterDefinition parameterDef)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();

            VariableDefinition localChannelVariableDef = CodegenSession.GeneralHelper.CreateVariable(methodDef, typeof(Channel));
            if (parameterDef != null)
                processor.Emit(OpCodes.Ldarg, parameterDef);
            else
                processor.Emit(OpCodes.Ldc_I4, (int)Channel.Reliable);

            //Set to local value.
            processor.Emit(OpCodes.Stloc, localChannelVariableDef);
            return localChannelVariableDef;
        }

        /// <summary>
        /// Creates a reader for a RPC.
        /// </summary>
        /// <param name="originalMethodDef"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns></returns>
        private MethodDefinition CreateRpcReaderMethod(TypeDefinition typeDef, MethodDefinition originalMethodDef, List<ParameterDefinition> serializedParameters, MethodDefinition logicMethodDef, CustomAttribute rpcAttribute, RpcType rpcType, out bool intentionallyNull)
        {
            intentionallyNull = false;

            

            string methodName = $"{READER_PREFIX}{GetMethodNameAsParameters(originalMethodDef)}";
            /* If method already exist then just return it. This
             * can occur when a method needs to be rebuilt due to
             * inheritence, and renumbering the RPC method names. 
             * The reader method however does not need to be rewritten. */
            MethodDefinition createdMethodDef = typeDef.GetMethod(methodName);
            //If found.
            if (createdMethodDef != null)
                return createdMethodDef;

            //Create the method body.
            createdMethodDef = new MethodDefinition(
                methodName,
                MethodAttributes.Private,
                originalMethodDef.Module.TypeSystem.Void);
            typeDef.Methods.Add(createdMethodDef);

            createdMethodDef.Body.InitLocals = true;

            if (rpcType == RpcType.Server)
                return CreateServerRpcReaderMethod(typeDef, originalMethodDef, createdMethodDef, serializedParameters, logicMethodDef, rpcAttribute);
            else if (rpcType == RpcType.Target || rpcType == RpcType.Observers)
                return CreateClientRpcReaderMethod(originalMethodDef, createdMethodDef, serializedParameters, logicMethodDef, rpcAttribute, rpcType);
            else
                return null;
        }


        /// <summary>
        /// Creates a reader for ServerRpc.
        /// </summary>
        /// <param name="originalMd"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns></returns>
        private MethodDefinition CreateServerRpcReaderMethod(TypeDefinition typeDef, MethodDefinition originalMd, MethodDefinition createdMd, List<ParameterDefinition> serializedParameters, MethodDefinition logicMd, CustomAttribute rpcAttribute)
        {
            ILProcessor createdProcessor = createdMd.Body.GetILProcessor();

            bool requireOwnership = rpcAttribute.GetField(REQUIREOWNERSHIP_NAME, true);
            //Create PooledReader parameter.
            ParameterDefinition readerParameterDef = CodegenSession.GeneralHelper.CreateParameter(createdMd, CodegenSession.ReaderHelper.PooledReader_TypeRef);

            //Add connection parameter to the read method. Internals pass the connection into this.
            ParameterDefinition channelParameterDef = GetOrCreateChannelParameter(createdMd, RpcType.Server);
            ParameterDefinition connectionParameterDef = GetOrCreateNetworkConnectionParameter(createdMd);
            /* It's very important to read everything
             * from the PooledReader before applying any
             * exit logic. Should the method return before
             * reading the data then anything after the rpc
             * packet will be malformed due to invalid index. */
            VariableDefinition[] readVariableDefs;
            List<Instruction> allReadInsts;
            CreateRpcReadInstructions(createdMd, readerParameterDef, serializedParameters, out readVariableDefs, out allReadInsts);

            Instruction retInst = CreateServerRpcConditionsForServer(createdProcessor, requireOwnership, connectionParameterDef);
            if (retInst != null)
                createdProcessor.InsertBefore(retInst, allReadInsts);
            //Read to clear pooledreader.
            createdProcessor.Add(allReadInsts);

            //this.Logic
            createdProcessor.Emit(OpCodes.Ldarg_0);
            //Add each read variable as an argument. 
            foreach (VariableDefinition vd in readVariableDefs)
                createdProcessor.Emit(OpCodes.Ldloc, vd);

            /* Pass in channel and connection if original
             * method supports them. */
            ParameterDefinition originalChannelParameterDef = GetChannelParameter(originalMd, RpcType.Server);
            ParameterDefinition originalConnectionParameterDef = GetNetworkConnectionParameter(originalMd);
            if (originalChannelParameterDef != null)
                createdProcessor.Emit(OpCodes.Ldarg, channelParameterDef);
            if (originalConnectionParameterDef != null)
                createdProcessor.Emit(OpCodes.Ldarg, connectionParameterDef);
            //Call __Logic method.
            createdProcessor.Emit(OpCodes.Call, logicMd);
            createdProcessor.Emit(OpCodes.Ret);

            return createdMd;
        }


        /// <summary>
        /// Creates a reader for ObserversRpc.
        /// </summary>
        /// <param name="originalMd"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns></returns>
        private MethodDefinition CreateClientRpcReaderMethod(MethodDefinition originalMd, MethodDefinition createdMd, List<ParameterDefinition> serializedParameters, MethodDefinition logicMethodDef, CustomAttribute rpcAttribute, RpcType rpcType)
        {
            ILProcessor createdProcessor = createdMd.Body.GetILProcessor();

            //Create PooledReader parameter.
            ParameterDefinition readerParameterDef = CodegenSession.GeneralHelper.CreateParameter(createdMd, CodegenSession.ReaderHelper.PooledReader_TypeRef);
            ParameterDefinition channelParameterDef = GetOrCreateChannelParameter(createdMd, rpcType);
            /* It's very important to read everything
             * from the PooledReader before applying any
             * exit logic. Should the method return before
             * reading the data then anything after the rpc
             * packet will be malformed due to invalid index. */
            VariableDefinition[] readVariableDefs;
            List<Instruction> allReadInsts;
            CreateRpcReadInstructions(createdMd, readerParameterDef, serializedParameters, out readVariableDefs, out allReadInsts);
            //Read instructions even if not to include owner.
            createdProcessor.Add(allReadInsts);

            /* ObserversRpc IncludeOwnerCheck. */
            if (rpcType == RpcType.Observers)
            {
                //If to not include owner then don't call logic if owner.
                bool includeOwner = rpcAttribute.GetField(INCLUDEOWNER_NAME, true);
                if (!includeOwner)
                {
                    //Create return if owner.
                    Instruction retInst = CodegenSession.ObjectHelper.CreateLocalClientIsOwnerCheck(createdMd, LoggingType.Off, true, true);
                    createdProcessor.InsertBefore(retInst, allReadInsts);
                }
            }

            createdProcessor.Emit(OpCodes.Ldarg_0); //this.

            /* TargetRpc passes in localconnection
            * as receiver for connection. */
            if (rpcType == RpcType.Target)
            {
                createdProcessor.Emit(OpCodes.Ldarg_0); //this.
                createdProcessor.Emit(OpCodes.Call, CodegenSession.ObjectHelper.NetworkBehaviour_LocalConnection_MethodRef);
            }

            //Add each read variable as an argument. 
            foreach (VariableDefinition vd in readVariableDefs)
                createdProcessor.Emit(OpCodes.Ldloc, vd);
            //Channel.
            ParameterDefinition originalChannelParameterDef = GetChannelParameter(originalMd, rpcType);
            if (originalChannelParameterDef != null)
                createdProcessor.Emit(OpCodes.Ldarg, channelParameterDef);
            //Call __Logic method.
            createdProcessor.Emit(OpCodes.Call, logicMethodDef);
            createdProcessor.Emit(OpCodes.Ret);

            return createdMd;
        }


        /// <summary>
        /// Gets the optional NetworkConnection parameter for ServerRpc, if it exists.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <returns></returns>
        private ParameterDefinition GetNetworkConnectionParameter(MethodDefinition methodDef)
        {

            ParameterDefinition result = methodDef.GetEndParameter(0);
            //Is null, not networkconnection, or doesn't have default.
            if (result == null || !result.Is(typeof(NetworkConnection)) || !result.HasDefault)
                return null;

            return result;
        }

        /// <summary>
        /// Creates a NetworkConnection parameter if it's not the last or second to last parameter.
        /// </summary>
        /// <param name="methodDef"></param>
        private ParameterDefinition GetOrCreateNetworkConnectionParameter(MethodDefinition methodDef)
        {
            ParameterDefinition result = GetNetworkConnectionParameter(methodDef);
            if (result == null)
                return CodegenSession.GeneralHelper.CreateParameter(methodDef, typeof(NetworkConnection), "conn");
            else
                return result;
        }

        /// <summary>
        /// Returns the Channel parameter if it exist.
        /// </summary>
        /// <param name="originalMethodDef"></param>
        private ParameterDefinition GetChannelParameter(MethodDefinition methodDef, RpcType rpcType)
        {
            ParameterDefinition result = null;
            ParameterDefinition pd = methodDef.GetEndParameter(0);
            if (pd != null)
            {
                //Last parameter is channel.
                if (pd.Is(typeof(Channel)))
                {
                    result = pd;
                }
                /* Only other end parameter may be networkconnection.
                 * This can only be checked if a ServerRpc. */
                else if (rpcType == RpcType.Server)
                {
                    //If last parameter is networkconnection and its default then can check second to last.
                    if (pd.Is(typeof(NetworkConnection)) && pd.HasDefault)
                    {
                        pd = methodDef.GetEndParameter(1);
                        if (pd != null && pd.Is(typeof(Channel)))
                            result = pd;
                    }
                }
                else
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a channel parameter if missing.
        /// </summary>
        /// <param name="originalMethodDef"></param>
        private ParameterDefinition GetOrCreateChannelParameter(MethodDefinition methodDef, RpcType rpcType)
        {
            ParameterDefinition result = GetChannelParameter(methodDef, rpcType);
            //Add channel parameter if not included.
            if (result == null)
            {
                ParameterDefinition connParameter = GetNetworkConnectionParameter(methodDef);
                //If the connection parameter is specified then channel has to go before it.
                if (connParameter != null)
                    return CodegenSession.GeneralHelper.CreateParameter(methodDef, typeof(Channel), "channel", ParameterAttributes.None, connParameter.Index);
                //Not specified, add channel at end.
                else
                    return CodegenSession.GeneralHelper.CreateParameter(methodDef, typeof(Channel), "channel");
            }
            else
            {
                return result;
            }
        }

        /// <summary>
        /// Creates a read for every writtenParameters and outputs variables read into, and instructions.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="methodDef"></param>
        /// <param name="readerParameterDef"></param>
        /// <param name="serializedParameters"></param>
        /// <param name="readVariableDefs"></param>
        /// <param name="allReadInsts"></param>
        private void CreateRpcReadInstructions(MethodDefinition methodDef, ParameterDefinition readerParameterDef, List<ParameterDefinition> serializedParameters, out VariableDefinition[] readVariableDefs, out List<Instruction> allReadInsts)
        {
            /* It's very important to read everything
            * from the PooledReader before applying any
            * exit logic. Should the method return before
            * reading the data then anything after the rpc
            * packet will be malformed due to invalid index. */
            readVariableDefs = new VariableDefinition[serializedParameters.Count];
            allReadInsts = new List<Instruction>();
            ILProcessor processor = methodDef.Body.GetILProcessor();

            //True if last parameter is a connection and a server rpc.
            for (int i = 0; i < serializedParameters.Count; i++)
            {
                //Get read instructions and insert it before the return.
                List<Instruction> insts = CodegenSession.ReaderHelper.CreateRead(methodDef, readerParameterDef, serializedParameters[i].ParameterType, out readVariableDefs[i]);
                allReadInsts.AddRange(insts);
            }

        }
        /// <summary>
        /// Creates conditions that clients must pass to send a ServerRpc.
        /// </summary>
        /// <param name="createdProcessor"></param>
        /// <param name="rpcAttribute"></param>
        private void CreateServerRpcConditionsForClient(MethodDefinition methodDef, CustomAttribute rpcAttribute)
        {
            bool requireOwnership = rpcAttribute.GetField(REQUIREOWNERSHIP_NAME, true);
            //If (!base.IsOwner);
            if (requireOwnership)
                CodegenSession.ObjectHelper.CreateLocalClientIsOwnerCheck(methodDef, LoggingType.Warning, false, true);
            //If (!base.IsClient)
            CodegenSession.ObjectHelper.CreateIsClientCheck(methodDef, LoggingType.Warning, false, true);
        }

        /// <summary>
        /// Creates conditions that server must pass to process a ServerRpc.
        /// </summary>
        /// <param name="createdProcessor"></param>
        /// <param name="rpcAttribute"></param>
        /// <returns>Ret instruction.</returns>
        private Instruction CreateServerRpcConditionsForServer(ILProcessor createdProcessor, bool requireOwnership, ParameterDefinition connectionParametereDef)
        {
            /* Don't need to check if server on receiving end.
             * Next compare connection with owner. */
            //If (!base.CompareOwner);
            if (requireOwnership)
                return CodegenSession.ObjectHelper.CreateRemoteClientIsOwnerCheck(createdProcessor, connectionParametereDef);
            else
                return null;
        }

        /// <summary>
        /// Creates conditions that server must pass to process a ClientRpc.
        /// </summary>
        /// <param name="createdProcessor"></param>
        private void CreateClientRpcConditionsForServer(MethodDefinition methodDef)
        {
            //If (!base.IsServer)
            CodegenSession.ObjectHelper.CreateIsServerCheck(methodDef, LoggingType.Warning, false, false);
        }

        /// <summary>
        /// Creates a method containing the logic which will run when receiving the Rpc.
        /// </summary>
        /// <param name="originalMethodDef"></param>
        /// <returns></returns>
        private MethodDefinition CreateRpcLogicMethod(TypeDefinition typeDef, bool runLocally, MethodDefinition originalMethodDef, List<ParameterDefinition> serializedParameters, RpcType rpcType, out bool intentionallyNull)
        {
            intentionallyNull = false;

            

            string methodName = $"{LOGIC_PREFIX}{GetMethodNameAsParameters(originalMethodDef)}";
            /* If method already exist then just return it. This
             * can occur when a method needs to be rebuilt due to
             * inheritence, and renumbering the RPC method names. 
             * The logic method however does not need to be rewritten. */
            MethodDefinition createdMethodDef = typeDef.GetMethod(methodName);
            //If found.
            if (createdMethodDef != null)
                return createdMethodDef;

            //Create the method body.
            createdMethodDef = new MethodDefinition(
            methodName, originalMethodDef.Attributes, originalMethodDef.ReturnType);
            typeDef.Methods.Add(createdMethodDef);
            createdMethodDef.Body.InitLocals = true;

            //Copy parameter expecations into new method.
            foreach (ParameterDefinition pd in originalMethodDef.Parameters)
                createdMethodDef.Parameters.Add(pd);

            //Swap bodies.
            (createdMethodDef.Body, originalMethodDef.Body) = (originalMethodDef.Body, createdMethodDef.Body);
            //Move over all the debugging information
            foreach (SequencePoint sequencePoint in originalMethodDef.DebugInformation.SequencePoints)
                createdMethodDef.DebugInformation.SequencePoints.Add(sequencePoint);
            originalMethodDef.DebugInformation.SequencePoints.Clear();

            foreach (CustomDebugInformation customInfo in originalMethodDef.CustomDebugInformations)
                createdMethodDef.CustomDebugInformations.Add(customInfo);
            originalMethodDef.CustomDebugInformations.Clear();
            //Swap debuginformation scope.
            (originalMethodDef.DebugInformation.Scope, createdMethodDef.DebugInformation.Scope) = (createdMethodDef.DebugInformation.Scope, originalMethodDef.DebugInformation.Scope);

            return createdMethodDef;
        }

        /// <summary>
        /// Finds and fixes call to base methods within remote calls
        /// <para>For example, changes `base.CmdDoSomething` to `base.UserCode_CmdDoSomething` within `this.UserCode_CmdDoSomething`</para>
        /// </summary>
        /// <param name="type"></param>
        /// <param name="createdMethodDef"></param>
        private void RedirectBaseCall(MethodDefinition createdMethodDef, MethodDefinition originalMethodDef)
        {
            //All logic RPCs end with the logic suffix.
            if (!createdMethodDef.Name.StartsWith(LOGIC_PREFIX))
                return;
            //Not virtual, no need to check.
            if (!createdMethodDef.IsVirtual)
                return;

            foreach (Instruction instruction in createdMethodDef.Body.Instructions)
            {
                // if call to base.RpcDoSomething within this.RpcDoSOmething.
                if (CodegenSession.GeneralHelper.IsCallToMethod(instruction, out MethodDefinition calledMethod) && calledMethod.Name == originalMethodDef.Name)
                {
                    MethodReference baseLogicMd = createdMethodDef.DeclaringType.GetMethodInBase(createdMethodDef.Name);
                    if (baseLogicMd == null)
                    {
                        CodegenSession.LogError($"Could not find base method for {createdMethodDef.Name}.");
                        return;
                    }

                    instruction.Operand = CodegenSession.ImportReference(baseLogicMd);
                }
            }
        }


        /// <summary> 
        /// Redirects calls from the original Rpc method to the writer method.
        /// </summary>
        /// <param name="originalMd"></param>
        /// <param name="writerMd"></param>
        private void RedirectRpcMethod(MethodDefinition originalMd, MethodDefinition writerMd, MethodDefinition logicMd, bool runLocally)
        {
            ILProcessor originalProcessor = originalMd.Body.GetILProcessor();
            originalMd.Body.Instructions.Clear();

            originalProcessor.Emit(OpCodes.Ldarg_0); //this.
                                                     //Parameters.
            foreach (ParameterDefinition pd in originalMd.Parameters)
                originalProcessor.Emit(OpCodes.Ldarg, pd);

            //Call method.
            MethodReference writerMethodRef = CodegenSession.ImportReference(writerMd);
            originalProcessor.Emit(OpCodes.Call, writerMethodRef);

            if (runLocally)
            {
                originalProcessor.Emit(OpCodes.Ldarg_0); //this.
                                                         //Parameters.
                foreach (ParameterDefinition pd in originalMd.Parameters)
                    originalProcessor.Emit(OpCodes.Ldarg, pd);
                originalProcessor.Emit(OpCodes.Call, logicMd);
            }


            originalProcessor.Emit(OpCodes.Ret);
        }


        #region CreateSend....

        /// <summary>
        /// Creates a call to SendServerRpc on NetworkBehaviour.
        /// </summary>
        /// <param name="writerVariableDef"></param>
        /// <param name="channel"></param>
        private void CreateSendServerRpc(MethodDefinition methodDef, uint methodHash, VariableDefinition writerVariableDef, VariableDefinition channelVariableDef)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();
            CreateSendRpcCommon(processor, methodHash, writerVariableDef, channelVariableDef);
            //Call NetworkBehaviour.
            processor.Emit(OpCodes.Call, CodegenSession.ObjectHelper.NetworkBehaviour_SendServerRpc_MethodRef);
        }

        /// <summary>
        /// Creates a call to SendObserversRpc on NetworkBehaviour.
        /// </summary>
        private void CreateSendObserversRpc(MethodDefinition methodDef, uint methodHash, VariableDefinition writerVariableDef, VariableDefinition channelVariableDef, CustomAttribute rpcAttribute)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();
            CreateSendRpcCommon(processor, methodHash, writerVariableDef, channelVariableDef);
            //Also add if buffered.
            bool bufferLast = rpcAttribute.GetField(BUFFERLAST_NAME, false);
            int buffered = (bufferLast) ? 1 : 0;

            //Warn user if any values are byref.
            bool usedByref = false;
            foreach (ParameterDefinition item in methodDef.Parameters)
            {
                if (item.IsIn)
                {
                    usedByref = true;
                    break;
                }
            }
            if (usedByref)
                CodegenSession.LogWarning($"Method {methodDef.FullName} takes an argument by reference. While this is supported, using BufferLast in addition to by reference arguements will buffer the value as it was serialized, not as it is when sending buffered.");

            processor.Emit(OpCodes.Ldc_I4, buffered);
            //Call NetworkBehaviour.
            processor.Emit(OpCodes.Call, CodegenSession.ObjectHelper.NetworkBehaviour_SendObserversRpc_MethodRef);
        }
        /// <summary>
        /// Creates a call to SendTargetRpc on NetworkBehaviour.
        /// </summary>
        private void CreateSendTargetRpc(MethodDefinition methodDef, uint methodHash, VariableDefinition writerVariableDef, VariableDefinition channelVariableDef, ParameterDefinition targetConnectionParameterDef)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();
            CreateSendRpcCommon(processor, methodHash, writerVariableDef, channelVariableDef);
            //Reference to NetworkConnection that RPC is going to.
            processor.Emit(OpCodes.Ldarg, targetConnectionParameterDef);
            //Call NetworkBehaviour.
            processor.Emit(OpCodes.Call, CodegenSession.ObjectHelper.NetworkBehaviour_SendTargetRpc_MethodRef);
        }

        /// <summary>
        /// Writes common properties that all SendRpc methods use.
        /// </summary>
        private void CreateSendRpcCommon(ILProcessor processor, uint methodHash, VariableDefinition writerVariableDef, VariableDefinition channelVariableDef)
        {
            processor.Emit(OpCodes.Ldarg_0); // argument: this
                                             //Hash argument. 
            processor.Emit(OpCodes.Ldc_I4, (int)methodHash);
            //reference to PooledWriter.
            processor.Emit(OpCodes.Ldloc, writerVariableDef);
            //reference to Channel.
            processor.Emit(OpCodes.Ldloc, channelVariableDef);
        }
        #endregion
    }
}