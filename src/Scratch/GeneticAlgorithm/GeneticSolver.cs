﻿//  * **********************************************************************************
//  * Copyright (c) Clinton Sheppard
//  * This source code is subject to terms and conditions of the MIT License.
//  * A copy of the license can be found in the License.txt file
//  * at the root of this distribution. 
//  * By using this source code in any fashion, you are agreeing to be bound by 
//  * the terms of the MIT License.
//  * You must not remove this notice from this software.
//  * **********************************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Scratch.GeneticAlgorithm.Strategies;
using Scratch.Ranges.RangeEnumeration;
using Scratch.ShuffleIEnumerable;

namespace Scratch.GeneticAlgorithm
{
    public class FitnessResult
    {
        public uint Value;
        public string UniqueKey;
        public int? UnitOfMeaningIndexHint;
    }

    public class GeneticSolver
    {
        private const int DefaultMaxGenerationsWithoutImprovement = 16384;
        private const int DefaultMinimumStrategyPercentage = 2;
        private const decimal DefaultMutationRate = 0.25m;
        private const int EliteParentCount = (int)(GaPopsize * GaElitrate);
        private const float GaElitrate = 0.10f;
        private const int GaPopsize = 2048;
        private const int MaxImprovmentsToKeepFromEachRound = 5;
        private const decimal SlideRate = 0.001m;

        private static decimal _slidingMutationRate = DefaultMutationRate;
        private readonly List<Pair<decimal, IChildGenerationStrategy>> _childGenerationStrategies;
        private readonly int _gaMaxGenerationsWithoutImprovement = DefaultMaxGenerationsWithoutImprovement;
        private readonly IChildGenerationStrategy _randomStrategy;
        private int _minimumStrategyPercentage = DefaultMinimumStrategyPercentage;
        private int _numberOfGenesInUnitOfMeaning = 1;
        private Random _random;
        private int _randomSeed;
        private readonly MutationMidUnitOfMeaning _mutationStrategy;

        public GeneticSolver()
            : this(DefaultMaxGenerationsWithoutImprovement)
        {
        }

        public GeneticSolver(int maxGenerationsWithoutImprovement)
            : this(maxGenerationsWithoutImprovement,
                   (from t in Assembly.GetExecutingAssembly().GetTypes()
                    where t.GetInterfaces().Contains(typeof(IChildGenerationStrategy))
                    where t.GetConstructor(Type.EmptyTypes) != null
                    select Activator.CreateInstance(t) as IChildGenerationStrategy).ToArray())
        {
        }

// ReSharper disable ParameterTypeCanBeEnumerable.Local
        public GeneticSolver(int maxGenerationsWithoutImprovement, ICollection<IChildGenerationStrategy> childGenerationStrategies)
// ReSharper restore ParameterTypeCanBeEnumerable.Local
        {
            _gaMaxGenerationsWithoutImprovement = maxGenerationsWithoutImprovement;

            _childGenerationStrategies = new List<Pair<decimal, IChildGenerationStrategy>>(
                childGenerationStrategies
                    .OrderBy(x => x.OrderBy)
                    .Select(x => new Pair<decimal, IChildGenerationStrategy>(100m / childGenerationStrategies.Count, x))
                );

            _randomStrategy = childGenerationStrategies.FirstOrDefault(x => x.GetType() == typeof(RandomGenes)) ?? new RandomGenes();
            _mutationStrategy = new MutationMidUnitOfMeaning();

            OnlyPermuteNewGenesWhileHillClimbing = true;
            DisplayGenes = (generation, fitness, genes, howCreated) => Console.WriteLine("Generation {0} fitness {1}: {2}", generation.ToString().PadLeft(_gaMaxGenerationsWithoutImprovement.ToString().Length), fitness, genes);
        }

        public Action<int, uint, string, string> DisplayGenes { get; set; }
        public bool DisplayHowCreatedPercentages { get; set; }
        public int MinimumStrategyPercentage
        {
            get { return _minimumStrategyPercentage; }
            set { _minimumStrategyPercentage = value; }
        }
        public int NumberOfGenesInUnitOfMeaning
        {
            get { return _numberOfGenesInUnitOfMeaning; }
            set { _numberOfGenesInUnitOfMeaning = value; }
        }
        public bool OnlyPermuteNewGenesWhileHillClimbing { get; set; }
        public int RandomSeed
        {
            set { _randomSeed = value; }
        }
        public bool UseFastSearch { get; set; }
        public bool UseHillClimbing { get; set; }

        private static IEnumerable<GeneSequence> CalcFitness(IList<GeneSequence> population, Func<string, FitnessResult> calcDistanceFromTarget)
        {
            for (int i = 0; i < GaPopsize; i++)
            {
                var geneSequence = population[i];
                if (geneSequence.Fitness.Value == GeneSequence.DefaultFitness.Value)
                {
                    geneSequence.Fitness = calcDistanceFromTarget(geneSequence.GetStringGenes());
                }
                yield return geneSequence;
            }
        }

        private static void CopyBestParents(IList<GeneSequence> population, IList<GeneSequence> buffer)
        {
            for (int i = 0; i < EliteParentCount; i++)
            {
                buffer[i] = population[i].Clone();
            }
        }

        private static void CopyPreviousBests(IEnumerable<GeneSequence> previousBests, IList<GeneSequence> buffer)
        {
            int lastBufferPosition = buffer.Count - 1;
            var source = previousBests.Shuffle().Take(EliteParentCount).ToList();
            for (int i = 0; i < EliteParentCount && i < source.Count; i++)
            {
                int indexFromEnd = lastBufferPosition - i;
                if (indexFromEnd < EliteParentCount)
                {
                    break;
                }
                buffer[indexFromEnd] = source[i].Clone();
            }
        }

        private void CreateNextGeneration(int freezeGenesUpTo, int numberOfGenesToUse, List<GeneSequence> population, IList<GeneSequence> buffer, Func<char> getRandomGene, IEnumerable<GeneSequence> previousBests)
        {
            CopyBestParents(population, buffer);
            CopyPreviousBests(previousBests, population);
            SortByFitness(population);

            GenerateChildren(freezeGenesUpTo, numberOfGenesToUse, population, buffer, getRandomGene);
        }

        private static int FitnessSort(GeneSequence x, GeneSequence y)
        {
            int result = x.Fitness.Value.CompareTo(y.Fitness.Value);
            if (result == 0)
            {
                result = y.Generation.CompareTo(x.Generation);
            }
            return result;
        }

        private void GenerateChildren(int freezeGenesUpTo, int numberOfGenesToUse, IEnumerable<GeneSequence> population, IList<GeneSequence> buffer, Func<char> getRandomGene)
        {
            var unique = new HashSet<string>();
            var parents = population.Where(x => unique.Add(x.GetStringGenes())).Take(50).ToList();

            for (int i = 0; i < GaPopsize; i++)
            {
                decimal percentage = (decimal)(100 * _random.NextDouble());
                foreach (var strategy in _childGenerationStrategies)
                {
                    if (percentage < strategy.First)
                    {
                        buffer[i] = strategy.Second.Generate(parents, numberOfGenesToUse, getRandomGene, NumberOfGenesInUnitOfMeaning, _slidingMutationRate, _random.Next, freezeGenesUpTo);
                        break;
                    }
                    percentage -= strategy.First;
                }
            }
        }

        public GeneSequence GetBestGenetically(int numberOfGenesToUse, string possibleGenes, Func<string, FitnessResult> calcFitness)
        {
            int seed = _randomSeed != 0 ? _randomSeed : (int)DateTime.Now.Ticks;
            Console.WriteLine("using random seed: " + seed);
            _random = new Random(seed);

            var popAlpha = new List<GeneSequence>(GaPopsize);
            var popBeta = new List<GeneSequence>(GaPopsize);

            Func<char> getRandomGene = () => possibleGenes[_random.Next(possibleGenes.Length)];

            InitPopulation(UseHillClimbing ? NumberOfGenesInUnitOfMeaning : numberOfGenesToUse, popAlpha, popBeta, getRandomGene);
            var population = popAlpha;
            var spare = popBeta;
            var previousBests = new List<GeneSequence>
                {
                    population.First()
                };
            previousBests[0].Fitness = calcFitness(previousBests[0].GetStringGenes());
            int generation = 0;

            var generationsBetweenImprovments = Enumerable.Repeat(_gaMaxGenerationsWithoutImprovement, 20).ToList();

            if (UseHillClimbing)
            {
                int failureToImproveCount = 0;
                var bestEver = new GeneSequence(new char[] { }, null)
                    {
                        Fitness = new FitnessResult{Value = UInt32.MaxValue}
                    };
                for (int i = NumberOfGenesInUnitOfMeaning; i < numberOfGenesToUse - 1; i += NumberOfGenesInUnitOfMeaning)
                {
                    GetBestGenetically(OnlyPermuteNewGenesWhileHillClimbing ? i - NumberOfGenesInUnitOfMeaning : 0, i, getRandomGene, previousBests, population, spare, calcFitness, generationsBetweenImprovments, ref generation);
                    var incrementalBest = previousBests.First();
                    if (incrementalBest.Fitness.Value < bestEver.Fitness.Value)
                    {
                        bestEver = incrementalBest.Clone();
                        failureToImproveCount = 0;
                    }
                    else
                    {
                        failureToImproveCount++;
                        if (incrementalBest.Fitness.Value > bestEver.Fitness.Value * 1.05m || failureToImproveCount >= 5)
                        {
                            Console.WriteLine("Fitness appears to be getting worse, returning best result: fitness " + bestEver.Fitness.Value + " in generation " + bestEver.Generation);
                            return bestEver;
                        }
                    }
                    var parents = previousBests.ToList();
                    population.Clear();
                    spare.Clear();

                    previousBests.Clear();
                    for (int j = 0; j < GaPopsize; j++)
                    {
                        var random = _randomStrategy.Generate(null, NumberOfGenesInUnitOfMeaning, getRandomGene, NumberOfGenesInUnitOfMeaning, _slidingMutationRate, _random.Next, 0);
                        var newChild = new GeneSequence(parents[j % parents.Count].Genes.Concat(random.Genes).ToArray(), _mutationStrategy)
                            {
                                Fitness = GeneSequence.DefaultFitness
                            };

                        population.Add(newChild);
                        spare.Add(newChild.Clone());
                    }
                    previousBests.AddRange(population.Select(x =>
                        {
                            x.Fitness = calcFitness(x.GetStringGenes());
                            return x;
                        }).Where(x => x.Fitness.Value < incrementalBest.Fitness.Value));
                    if (previousBests.Count < 100)
                    {
                        previousBests.AddRange(population.OrderBy(x => x.Fitness.Value).Take(100 - previousBests.Count));
                    }
                    SortByFitness(previousBests);
                    int count = 1 + (i / NumberOfGenesInUnitOfMeaning);
                    Console.WriteLine("> " + count);
                    if (previousBests[0].Fitness.Value < incrementalBest.Fitness.Value)
                    {
                        PrintBest(generation, previousBests[0]);
                    }
                }
                return bestEver;
            }

            GetBestGenetically(0, numberOfGenesToUse, getRandomGene, previousBests, population, spare, calcFitness, generationsBetweenImprovments, ref generation);
            var best = previousBests.First();
            return best;
        }

        private void GetBestGenetically(int freezeGenesUpTo, int numberOfGenesToUse, Func<char> getRandomGene, List<GeneSequence> previousBests, List<GeneSequence> population, List<GeneSequence> spare, Func<string, FitnessResult> calcFitness, ICollection<int> generationsBetweenImprovments, ref int generation)
        {
            _slidingMutationRate = DefaultMutationRate;
            int fastSearchPopulationSize = GaPopsize / 10 + 4 * numberOfGenesToUse / _numberOfGenesInUnitOfMeaning;
            int maxGenerationsWithoutImprovement = (int)(generationsBetweenImprovments.Average() * 1.5);
            Console.WriteLine("> max generations to run without improvement: " + maxGenerationsWithoutImprovement);
            int maxGenerationsWithoutNewSequences = maxGenerationsWithoutImprovement / 10;
            int generationsWithoutNewSequences = 0;
            int i = 0;
            for (; i < maxGenerationsWithoutImprovement && generationsWithoutNewSequences <= maxGenerationsWithoutNewSequences; i++, generation++)
            {
                var previousBestLookup = new HashSet<string>(previousBests.Select(x => x.Fitness.UniqueKey ?? x.GetStringGenes()));
                var populationWithFitness = CalcFitness(population, calcFitness);

                var first = populationWithFitness.First();
                var worstFitness = previousBests[previousBests.Count / 2].Fitness.Value;
                var newSequences = populationWithFitness
                    .Take(UseFastSearch /*&& i < 20*/ ? fastSearchPopulationSize : GaPopsize)
                    .Where(x => x.Fitness.Value <= worstFitness)
                    .Where(x => previousBestLookup.Add(x.Fitness.UniqueKey ?? x.GetStringGenes()))
                    .Take(UseFastSearch ? MaxImprovmentsToKeepFromEachRound : (int)((1 - _slidingMutationRate) * GaPopsize))
                    .ToList();

                if (newSequences.Any())
                {
                    generationsWithoutNewSequences = 0;
                    SortByFitness(newSequences);
                    var previousBestFitness = previousBests.First().Fitness.Value;
                    if (newSequences.First().Fitness.Value < previousBestFitness)
                    {
                        PrintBest(generation, newSequences.First());
//                        Console.WriteLine("> improved after generation " + i);
                        generationsBetweenImprovments.Add(i);
                        i = -1;
                    }
                    foreach (var copy in newSequences.Select(geneSequence => geneSequence.Clone()))
                    {
                        copy.Generation = generation;
                        previousBests.Add(copy);
                    }
                    int numberToKeep = Math.Max(100, previousBests.Count(x => x.Fitness.Value == first.Fitness.Value));
                    SortByFitness(previousBests);
                    if (numberToKeep < previousBests.Count)
                    {
                        previousBests.RemoveRange(numberToKeep, previousBests.Count - numberToKeep);
                    }
                    UpdateStrategyPercentages(previousBests, generation);

                    _slidingMutationRate = DefaultMutationRate;
                }
                else
                {
                    generationsWithoutNewSequences++;
                    if (generationsWithoutNewSequences > maxGenerationsWithoutNewSequences)
                    {
                        break;
                    }
                    _slidingMutationRate = Math.Max(_slidingMutationRate - SlideRate, 0);
                }

                if (first.Fitness.Value == 0)
                {
                    break;
                }

                CreateNextGeneration(freezeGenesUpTo, numberOfGenesToUse, population, spare, getRandomGene, previousBests);
                var temp = spare;
                spare = population;
                population = temp;
            }

            generationsBetweenImprovments.Add(i);

//            previousBests.First().GetStringGenes();
        }

        private void InitPopulation(int numberOfGenesToUse, ICollection<GeneSequence> population, List<GeneSequence> buffer, Func<char> getRandomGene)
        {
            population.Clear();
            buffer.Clear();
            for (int i = 0; i < GaPopsize; i++)
            {
                population.Add(_randomStrategy.Generate(null, numberOfGenesToUse, getRandomGene, NumberOfGenesInUnitOfMeaning, _slidingMutationRate, _random.Next, 0));
            }

            buffer.AddRange(population.Select(x => x.Clone()));
        }

        private void PrintBest(int generation, GeneSequence geneSequence)
        {
            DisplayGenes(1 + generation, geneSequence.Fitness.Value, geneSequence.GetStringGenes(), geneSequence.Strategy.Description);
        }

        private static void SortByFitness(List<GeneSequence> population)
        {
            population.Sort(FitnessSort);
        }

        private void UpdateStrategyPercentages(ICollection<GeneSequence> previousBests, int generation)
        {
            int minimumStrategyPercentageValue = (int)Math.Ceiling(MinimumStrategyPercentage / 100m * previousBests.Count);
            var strategiesInUse = previousBests
                .Select(x => x.Strategy)
                .GroupBy(x => x)
                .Where(x => x.Count() >= minimumStrategyPercentageValue)
                .ToList();
            int adjustedPreviousBestsCount = previousBests.Count
                                             + (_childGenerationStrategies.Count - strategiesInUse.Count) * minimumStrategyPercentageValue;

            foreach (var strategy in _childGenerationStrategies)
            {
                bool found = false;
                foreach (var strategyInUse in strategiesInUse)
                {
                    if (strategy.Second == strategyInUse.Key)
                    {
                        strategy.First = 100.0m * Math.Max(minimumStrategyPercentageValue, strategyInUse.Count()) / adjustedPreviousBestsCount;
                        found = true;
                    }
                }
                if (!found)
                {
                    strategy.First = 0;
                }
            }

            // normalize to 100 %
            decimal strategySum = _childGenerationStrategies.Sum(x => Math.Max(MinimumStrategyPercentage, x.First));
            foreach (var strategy in _childGenerationStrategies)
            {
                strategy.First = 100.0m * Math.Max(MinimumStrategyPercentage, strategy.First) / strategySum;
            }

            if (generation % 100 == 0)
            {
                var strategyPercentages = _childGenerationStrategies.Select(x => x.Second.Description + " " + (x.First < 10 ? " " : "") + Math.Round(x.First, 1).ToString().PadRight(x.First < 10 ? 3 : 4)).ToArray();
                Console.WriteLine("% " + String.Join(" ", strategyPercentages));
            }
        }
    }
}