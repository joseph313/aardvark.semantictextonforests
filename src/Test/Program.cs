﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aardvark.Base;
using LibSvm;

namespace Test
{
    class Program
    {
        static IEnumerable<Parameter> Search()
        {
            for (var gamma = 0.0; gamma < 10.0; gamma += 0.1)
            {
                Console.WriteLine($"search {gamma}/5.0");
                for (var C = 2.0; C <= 10; C += 0.1)
                {
                    yield return new Parameter
                    {
                        SvmType = SvmType.C_SVC,
                        KernelType = KernelType.POLY,
                        Degree = 3,
                        Gamma = gamma,
                        Coef0 = 1,
                        CacheSize = 1000,
                        Eps = 0.001,
                        C = C,
                        Weight = new double[0],
                        WeightLabel = new int[0],
                        Nu = 0,
                        p = 0.1,
                        Shrinking = 1,
                        Probability = 0
                    };
                }
            }
        }

        static void Main(string[] args)
        {
            var heart_scale = Svm.ReadProblem(@"C:\Data\Development\libsvm\heart_scale");

            var parameter = new Parameter
            {
                SvmType = SvmType.C_SVC,
                KernelType = KernelType.SIGMOID,
                Degree = 0,
                Gamma = 0.1,
                Coef0 = 0,
                CacheSize = 1000,
                Eps = 0.001,
                C = 100,
                Weight = new double[0],
                WeightLabel = new int[0],
                Nu = 0,
                p = 0.1,
                Shrinking = 1,
                Probability = 0
            };

            Console.WriteLine("check: '{0}'", Svm.CheckParameter(heart_scale, parameter));

            //var learn = new Problem(
            //    heart_scale.x.TakePeriodic(2).ToArray(),
            //    heart_scale.y.TakePeriodic(2).ToArray()
            //    );
            //var model = Svm.Train(learn, parameter);

            var bestNok = heart_scale.Count + 1;
            foreach (var p in Search())
            {
                var model = Svm.Train(heart_scale, p);

                //var validation = Svm.CrossValidation(heart_scale, parameter, 10);
                //var checkprop = Svm.CheckProbabilityModel(model);

                var ok = 0;
                var nok = 0;
                for (var i = 0; i < heart_scale.x.Length; i++)
                {
                    var prediction = Svm.Predict(model, heart_scale.x[i]);
                    if (prediction == heart_scale.y[i]) ok++; else nok++;
                    //Console.WriteLine($"{heart_scale.y[i],3}    {prediction,-3}");
                }
                if (nok < bestNok)
                {
                    bestNok = nok;
                    Console.WriteLine($"ok: {ok,4}, nok: {nok,4}, gamma = {p.Gamma,8}, C = {p.C,8}");
                }

                GC.Collect();
            }
        }
    }
}