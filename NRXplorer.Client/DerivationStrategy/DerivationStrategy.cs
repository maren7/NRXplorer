﻿using NRealbit;
using NRXplorer.DerivationStrategy;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NRXplorer.DerivationStrategy
{
	public class DerivationStrategyOptions
	{
		public ScriptPubKeyType ScriptPubKeyType { get; set; }

		/// <summary>
		/// If true, in case of multisig, do not reorder the public keys of an address lexicographically (default: false)
		/// </summary>
		public bool KeepOrder
		{
			get; set;
		}
		
		public ReadOnlyDictionary<string, bool> AdditionalOptions { get; set; }
	}
	public class DerivationStrategyFactory
	{

		private readonly Network _Network;
		public Network Network
		{
			get
			{
				return _Network;
			}
		}
		public DerivationStrategyFactory(Network network)
		{
			if(network == null)
				throw new ArgumentNullException(nameof(network));
			_Network = network;
			if (_Network.Consensus.SupportSegwit)
			{
				AuthorizedOptions.Add("p2sh");
			}
			AuthorizedOptions.Add("keeporder");
			AuthorizedOptions.Add("legacy");
		}

		public HashSet<string> AuthorizedOptions { get; } = new HashSet<string>();

		readonly Regex MultiSigRegex = new Regex("^([0-9]{1,2})-of(-[A-Za-z0-9]+)+$");
		static DirectDerivationStrategy DummyPubKey = new DirectDerivationStrategy(new ExtKey().Neuter().GetWif(Network.RegTest), false);
		public DerivationStrategyBase Parse(string str)
		{
			var strategy = ParseCore(str);
			return strategy;
		}

		private DerivationStrategyBase ParseCore(string str)
		{
			bool legacy = false;
			bool p2sh = false;
			bool keepOrder = false;

			Dictionary<string, bool> optionsDictionary = new Dictionary<string, bool>(5);
			foreach (Match optionMatch in _OptionRegex.Matches(str))
			{
				var key = optionMatch.Groups[1].Value.ToLowerInvariant();
				if (!AuthorizedOptions.Contains(key))
					throw new FormatException($"The option '{key}' is not supported by this network");
				if (!optionsDictionary.TryAdd(key, true))
					throw new FormatException($"The option '{key}' is duplicated");
			}
			str = _OptionRegex.Replace(str, string.Empty);
			if (optionsDictionary.Remove("legacy"))
			{
				legacy = true;
			}
			if (optionsDictionary.Remove("p2sh"))
			{
				p2sh = true;
			}
			if (optionsDictionary.Remove("keeporder"))
			{
				keepOrder = true;
			}
			if (!legacy && !_Network.Consensus.SupportSegwit)
				throw new FormatException("Segwit is not supported you need to specify option '-[legacy]'");

			if (legacy && p2sh)
				throw new FormatException("The option 'legacy' is incompatible with 'p2sh'");

			var options = new DerivationStrategyOptions()
			{
				KeepOrder = keepOrder,
				ScriptPubKeyType = legacy ? ScriptPubKeyType.Legacy :
									p2sh ? ScriptPubKeyType.SegwitP2SH :
									ScriptPubKeyType.Segwit,
				AdditionalOptions = new ReadOnlyDictionary<string, bool>(optionsDictionary)
			};
			var match = MultiSigRegex.Match(str);
			if(match.Success)
			{
				var sigCount = int.Parse(match.Groups[1].Value);
				var pubKeys = match.Groups
									.OfType<Group>()
									.Skip(2)
									.SelectMany(g => g.Captures.OfType<Capture>())
									.Select(g => new RealbitExtPubKey(g.Value.Substring(1), Network))
									.ToArray();
				return CreateMultiSigDerivationStrategy(pubKeys, sigCount, options);
			}
			else
			{
				var key = _Network.Parse<RealbitExtPubKey>(str);
				return CreateDirectDerivationStrategy(key, options);
			}
		}

		/// <summary>
		/// Create a single signature derivation strategy from public key
		/// </summary>
		/// <param name="publicKey">The public key of the wallet</param>
		/// <param name="options">Derivation options</param>
		/// <returns></returns>
		public DerivationStrategyBase CreateDirectDerivationStrategy(ExtPubKey publicKey, DerivationStrategyOptions options = null)
		{
			return CreateDirectDerivationStrategy(publicKey.GetWif(Network), options);
		}

		/// <summary>
		/// Create a single signature derivation strategy from public key
		/// </summary>
		/// <param name="publicKey">The public key of the wallet</param>
		/// <param name="options">Derivation options</param>
		/// <returns></returns>
		public DerivationStrategyBase CreateDirectDerivationStrategy(RealbitExtPubKey publicKey, DerivationStrategyOptions options = null)
		{
			options = options ?? new DerivationStrategyOptions();
			DerivationStrategyBase strategy = new DirectDerivationStrategy(publicKey, options.ScriptPubKeyType != ScriptPubKeyType.Legacy, options.AdditionalOptions);
			if(options.ScriptPubKeyType != ScriptPubKeyType.Legacy && !_Network.Consensus.SupportSegwit)
				throw new InvalidOperationException("This crypto currency does not support segwit");

			if(options.ScriptPubKeyType == ScriptPubKeyType.SegwitP2SH)
			{
				strategy = new P2SHDerivationStrategy(strategy, true);
			}
			return strategy;
		}

		/// <summary>
		/// Create a multisig derivation strategy from public keys
		/// </summary>
		/// <param name="pubKeys">The public keys belonging to the multi sig</param>
		/// <param name="sigCount">The number of required signature</param>
		/// <param name="options">Derivation options</param>
		/// <returns>A multisig derivation strategy</returns>
		public DerivationStrategyBase CreateMultiSigDerivationStrategy(ExtPubKey[] pubKeys, int sigCount, DerivationStrategyOptions options = null)
		{
			return CreateMultiSigDerivationStrategy(pubKeys.Select(p => p.GetWif(Network)).ToArray(), sigCount, options);
		}

		/// <summary>
		/// Create a multisig derivation strategy from public keys
		/// </summary>
		/// <param name="pubKeys">The public keys belonging to the multi sig</param>
		/// <param name="sigCount">The number of required signature</param>
		/// <param name="options">Derivation options</param>
		/// <returns>A multisig derivation strategy</returns>
		public DerivationStrategyBase CreateMultiSigDerivationStrategy(RealbitExtPubKey[] pubKeys, int sigCount, DerivationStrategyOptions options = null)
		{
			options = options ?? new DerivationStrategyOptions();
			DerivationStrategyBase derivationStrategy = new MultisigDerivationStrategy(sigCount, pubKeys.ToArray(), options.ScriptPubKeyType == ScriptPubKeyType.Legacy, !options.KeepOrder, options.AdditionalOptions);
			if(options.ScriptPubKeyType == ScriptPubKeyType.Legacy)
				return new P2SHDerivationStrategy(derivationStrategy, false);

			if(!_Network.Consensus.SupportSegwit)
				throw new InvalidOperationException("This crypto currency does not support segwit");
			derivationStrategy = new P2WSHDerivationStrategy(derivationStrategy);
			if(options.ScriptPubKeyType == ScriptPubKeyType.SegwitP2SH)
			{
				derivationStrategy = new P2SHDerivationStrategy(derivationStrategy, true);
			}
			return derivationStrategy;
		}

		private void ReadBool(ref string str, string attribute, ref bool value)
		{
			value = str.Contains($"[{attribute}]");
			if(value)
			{
				str = str.Replace($"[{attribute}]", string.Empty);
				str = str.Replace("--", "-");
				if(str.EndsWith("-"))
					str = str.Substring(0, str.Length - 1);
			}
		}

		readonly static Regex _OptionRegex = new Regex(@"-\[([^ \]\-]+)\]");
	}
}
