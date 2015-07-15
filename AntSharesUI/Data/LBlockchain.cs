﻿using AntShares.Core;
using AntShares.IO;
using AntShares.Properties;
using LevelDB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AntShares.Data
{
    internal class LBlockchain : Blockchain, IDisposable
    {
        private DB db;

        public override bool IsReadOnly
        {
            get
            {
                return false;
            }
        }

        public LBlockchain()
        {
            Slice initialized;
            db = DB.Open(Settings.Default.DataDirectoryPath);
            if (!db.TryGet(ReadOptions.Default, SliceBuilder.Begin(DataEntryPrefix.Configuration).Add("initialized"), out initialized) || !initialized.ToBoolean())
            {
                OnBlock(Blockchain.GenesisBlock);
                db.Put(WriteOptions.Default, SliceBuilder.Begin(DataEntryPrefix.Configuration).Add("version"), 0);
                db.Put(WriteOptions.Default, SliceBuilder.Begin(DataEntryPrefix.Configuration).Add("initialized"), true);
            }
        }

        public void Dispose()
        {
            if (db != null)
            {
                db.Dispose();
                db = null;
            }
        }

        public override IEnumerable<RegisterTransaction> GetAssets()
        {
            yield return Blockchain.AntCoin;
            using (Iterator it = db.NewIterator(ReadOptions.Default))
            {
                for (it.Seek(SliceBuilder.Begin(DataEntryPrefix.IX_Register)); it.Valid() && it.Key() < SliceBuilder.Begin(DataEntryPrefix.IX_Register + 1); it.Next())
                {
                    yield return it.Value().ToArray().AsSerializable<RegisterTransaction>();
                }
            }
        }

        public override long GetQuantityIssued(UInt256 asset_id)
        {
            if (asset_id == AntCoin.Hash) throw new ArgumentException();
            Slice quantity = 0L;
            db.TryGet(ReadOptions.Default, SliceBuilder.Begin(DataEntryPrefix.ST_QuantityIssued).Add(asset_id), out quantity);
            return quantity.ToInt64();
        }

        public override Transaction GetTransaction(UInt256 hash)
        {
            Transaction tx = base.GetTransaction(hash);
            if (tx == null)
            {
                Slice value;
                if (db.TryGet(ReadOptions.Default, SliceBuilder.Begin(DataEntryPrefix.Transaction).Add(hash), out value))
                {
                    tx = Transaction.DeserializeFrom(value.ToArray());
                }
            }
            return tx;
        }

        protected override void OnBlock(Block block)
        {
            Dictionary<UInt256, long> assets = new Dictionary<UInt256, long>();
            WriteBatch batch = new WriteBatch();
            batch.Put(SliceBuilder.Begin(DataEntryPrefix.Block).Add(block.Hash), block.Trim());
            foreach (Transaction tx in block.Transactions)
            {
                batch.Put(SliceBuilder.Begin(DataEntryPrefix.Transaction).Add(tx.Hash), tx.ToArray());
                if (tx.Type == TransactionType.RegisterTransaction)
                {
                    RegisterTransaction reg_tx = (RegisterTransaction)tx;
                    batch.Put(SliceBuilder.Begin(DataEntryPrefix.IX_Register).Add((byte)reg_tx.RegisterType).Add(reg_tx.Hash), reg_tx.ToArray());
                }
                else if (tx.Type == TransactionType.IssueTransaction)
                {
                    foreach (var asset in tx.Outputs.GroupBy(p => p.AssetId).Where(g => g.All(p => p.Value > 0)).Select(g => new
                    {
                        AssetId = g.Key,
                        Sum = g.Sum(p => p.Value)
                    }))
                    {
                        if (assets.ContainsKey(asset.AssetId))
                        {
                            assets[asset.AssetId] += asset.Sum;
                        }
                        else
                        {
                            assets.Add(asset.AssetId, asset.Sum);
                        }
                    }
                }
                for (ushort index = 0; index < tx.Outputs.Length; index++)
                {
                    batch.Put(SliceBuilder.Begin(DataEntryPrefix.Unspent).Add(tx.Hash).Add(index), tx.Outputs[index].ToArray());
                }
            }
            foreach (TransactionInput input in block.Transactions.SelectMany(p => p.GetAllInputs()))
            {
                batch.Delete(SliceBuilder.Begin(DataEntryPrefix.Unspent).Add(input.PrevTxId).Add(input.PrevIndex));
            }
            foreach (var asset in assets)
            {
                Slice amount = 0L;
                db.TryGet(ReadOptions.Default, SliceBuilder.Begin(DataEntryPrefix.ST_QuantityIssued).Add(asset.Key), out amount);
                batch.Put(SliceBuilder.Begin(DataEntryPrefix.ST_QuantityIssued).Add(asset.Key), amount.ToInt64() + asset.Value);
            }
            db.Write(WriteOptions.Default, batch);
        }
    }
}