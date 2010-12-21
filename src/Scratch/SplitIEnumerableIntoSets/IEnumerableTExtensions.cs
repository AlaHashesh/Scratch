//  * **********************************************************************************
//  * Copyright (c) Clinton Sheppard
//  * This source code is subject to terms and conditions of the MIT License.
//  * A copy of the license can be found in the License.txt file
//  * at the root of this distribution. 
//  * By using this source code in any fashion, you are agreeing to be bound by 
//  * the terms of the MIT License.
//  * You must not remove this notice from this software.
//  * **********************************************************************************
using System.Collections.Generic;
using System.Linq;

namespace Scratch.SplitIEnumerableIntoSets
{
    /// <summary>
    /// http://stackoverflow.com/questions/1034429/how-to-prevent-memory-overflow-when-using-an-ienumerablet-and-linq-to-sql/1035039#1035039
    /// http://stackoverflow.com/questions/2222292/optimize-this-slow-linq-to-objects-query/2374520#2374520
    /// http://stackoverflow.com/questions/4461367/linq-to-objects-return-pairs-of-numbers-from-list-of-numbers/4471596#4471596
    /// </summary>
    public static class IEnumerableExtensions
    {
        public static IEnumerable<List<T>> InSetsOf<T>(this IEnumerable<T> source, int max)
        {
            return InSetsOf(source, max, false, default(T));
        }

        public static IEnumerable<List<T>> InSetsOf<T>(this IEnumerable<T> source, int max, bool fill, T fillValue)
        {
            var toReturn = new List<T>(max);
            foreach (var item in source)
            {
                toReturn.Add(item);
                if (toReturn.Count == max)
                {
                    yield return toReturn;
                    toReturn = new List<T>(max);
                }
            }
            if (toReturn.Any())
            {
                if (fill)
                {
                    toReturn.AddRange(Enumerable.Repeat(fillValue, max - toReturn.Count));
                }
                yield return toReturn;
            }
        }
    }
}