﻿using Aardvark.Base;
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ScratchAttila
{
    #region Semantic Texton Forest

    public class DataPoint
    {
        public ImagePatch Image;
        public int X;
        public int Y;
        public double PointWeight;
        public int label = -2;      //-2 if unknown label; label index else (we take -2 because libsvm sometimes likes to assign -1 to classes)

        [JsonIgnore]
        public V2i PixelCoords
        {
            get { return new V2i(X, Y); }
            set { X = value.X; Y = value.Y; }
        }
    }

    public class DataPointSet
    {
        public DataPoint[] DPSet;
        public double SetWeight;        //<-- this is not currently used, but needs to be implemented. TODO

        public DataPointSet()
        {
            DPSet = new DataPoint[] { };
        }

        public static DataPointSet operator+(DataPointSet current, DataPointSet other)
        {
            var result = new DataPointSet();

            var resultList = new List<DataPoint>();
            resultList.AddRange(current.DPSet);
            resultList.AddRange(other.DPSet);

            result.DPSet = resultList.ToArray();
            result.SetWeight = current.SetWeight + other.SetWeight;

            return result;
        }
    }

    public class Feature
    {
        public double Value;
    }

    public abstract class IFeatureProvider
    {
        public abstract void Init(int pixelWindowSize);

        public abstract Feature getFeature(DataPoint point);

        public Feature[] getArrayOfFeatures(DataPointSet points)
        {
            List<Feature> result = new List<Feature>();

            foreach(var point in points.DPSet)
            {
                result.Add(this.getFeature(point));
            }

            return result.ToArray();
        }
    }

    public abstract class ISamplingProvider
    {
        public abstract void init(int pixWindowSize);
        public abstract DataPointSet getDataPoints(ImagePatch image);
        public abstract DataPointSet getDataPoints(LabeledImage[] labelledImages);
    }

    public class Decider
    {
        public IFeatureProvider FeatureProvider;
        public ISamplingProvider SamplingProvider;
        public double DecisionThreshold;
        public double Certainty;
        
        //true = left, false = right
        public bool Decide(DataPoint dataPoint)
        {
            //var datapoints = SamplingProvider.getDataPoints(img);
            var feature = FeatureProvider.getFeature(dataPoint);
            var value = feature.Value;

            if (value < DecisionThreshold)
            {
                Report.Line(4, "Decided left at threshold " + DecisionThreshold);
                return true;
            }
            else
            {
                Report.Line(4, "Decided right at threshold " + DecisionThreshold);
                return false;
            }
        }

        //returns true if this node should be a leaf and leaves the out params as null; false else and fills the out params with the split values
        public Algo.DeciderTrainingResult InitializeDecision(DataPointSet currentDatapoints, ClassDistribution classDist, TrainingParams parameters, out DataPointSet leftRemaining, out DataPointSet rightRemaining, out ClassDistribution leftClassDist, out ClassDistribution rightClassDist)
        {
            //get a bunch of candidates for decision using the supplied featureProvider and samplingProvider, select the best one based on entropy, return either the 
            //left/right split subsets and false, or true if this node should be a leaf

            var threshCandidates = new double[parameters.ThresholdCandidateNumber];
            for (int i = 0; i < threshCandidates.Length; i++)
            {
                threshCandidates[i] = Algo.rand.NextDouble();
            }

            var bestThreshold = -1.0d;
            var bestScore = double.MinValue;
            var bestLeftSet = new DataPointSet();
            var bestRightSet = new DataPointSet();
            ClassDistribution bestLeftClassDist = null;
            ClassDistribution bestRightClassDist = null;

            bool inputIsEmpty = currentDatapoints.DPSet.Length == 0; //there is no image, no split is possible -> leaf
            bool inputIsOne = currentDatapoints.DPSet.Length == 1;   //there is exactly one image, no split is possible -> passthrough

            if (!inputIsEmpty && !inputIsOne)
            {

                foreach (var curThresh in threshCandidates)
                {
                    var currentLeftSet = new DataPointSet();
                    var currentRightSet = new DataPointSet();
                    ClassDistribution currentLeftClassDist = null;
                    ClassDistribution currentRightClassDist = null;

                    splitDatasetWithThreshold(currentDatapoints, curThresh, parameters, out currentLeftSet, out currentRightSet, out currentLeftClassDist, out currentRightClassDist);
                    double leftEntr = calcEntropy(currentLeftClassDist);
                    double rightEntr = calcEntropy(currentRightClassDist);

                    //from semantic texton paper -> maximize the score value
                    double leftWeight = (-1.0d) * currentLeftClassDist.getClassDistSum() / classDist.getClassDistSum();
                    double rightWeight = (-1.0d) * currentRightClassDist.getClassDistSum() / classDist.getClassDistSum();
                    double score = leftWeight * leftEntr + rightWeight * rightEntr;

                    if (score > bestScore) //new best threshold found
                    {
                        bestScore = score;
                        bestThreshold = curThresh;

                        bestLeftSet = currentLeftSet;
                        bestRightSet = currentRightSet;
                        bestLeftClassDist = currentLeftClassDist;
                        bestRightClassDist = currentRightClassDist;
                    }
                }
            }


            bool isLeaf = inputIsEmpty;   //no images reached this node

            if (parameters.ForcePassthrough) //if passthrough mode is active, never create a leaf inside the tree (force-fill the tree)
            {
                isLeaf = false;
            }

            bool passThrough = (Math.Abs(bestScore) < parameters.ThresholdInformationGainMinimum) || inputIsOne;  //no more information gain => copy the parent node

            Certainty = bestScore;

            if (isLeaf)
            {
                leftRemaining = null;
                rightRemaining = null;
                leftClassDist = null;
                rightClassDist = null;
                return Algo.DeciderTrainingResult.Leaf;
            }

            if (!passThrough && !isLeaf)  //reports for passthrough and leaf nodes are printed in Node.train method
            {
                Report.Line(3, "NN t:" + bestThreshold + " s:" + bestScore + "; dp=" + currentDatapoints.DPSet.Length + " l/r=" + bestLeftSet.DPSet.Length + "/" + bestRightSet.DPSet.Length + ((isLeaf) ? "->leaf" : ""));
            }

            this.DecisionThreshold = bestThreshold;
            leftRemaining = bestLeftSet;
            rightRemaining = bestRightSet;
            leftClassDist = bestLeftClassDist;
            rightClassDist = bestRightClassDist;

            if (passThrough || isLeaf)
            {
                return Algo.DeciderTrainingResult.PassThrough;
            }

            return Algo.DeciderTrainingResult.InnerNode;
        }

        //splits up the dataset using a threshold
        private void splitDatasetWithThreshold(DataPointSet dps, double threshold, TrainingParams parameters, out DataPointSet leftSet, out DataPointSet rightSet, out ClassDistribution leftDist, out ClassDistribution rightDist)
        {
            var leftList = new List<DataPoint>();
            var rightList = new List<DataPoint>();

            int targetFeatureCount = Math.Min(dps.DPSet.Length, parameters.MaxSampleCount);
            var actualDPS = dps.DPSet.GetRandomSubset(targetFeatureCount);

            foreach (var dp in actualDPS)
            {
                //select only a subset of features
                var feature = FeatureProvider.getFeature(dp);

                if (feature.Value < threshold)
                {
                    leftList.Add(dp);
                }
                else
                {
                    rightList.Add(dp);
                }

            }

            leftSet = new DataPointSet();
            rightSet = new DataPointSet();

            leftSet.DPSet = leftList.ToArray();
            rightSet.DPSet = rightList.ToArray();

            leftDist = new ClassDistribution(GlobalParams.Labels, leftSet);
            rightDist = new ClassDistribution(GlobalParams.Labels, rightSet);
        }

        //calculates the entropy of one class distribution as input to the score calculation
        private double calcEntropy(ClassDistribution dist)
        {
            //from http://en.wikipedia.org/wiki/ID3_algorithm

            double sum = 0;
            //foreach(var cl in dist.ClassLabels)
            foreach (var cl in GlobalParams.Labels)
            {
                var px = dist.getClassProbability(cl);
                if(px == 0)
                {
                    continue;
                }
                var val = (px * Math.Log(px, 2));
                sum = sum + val;
            }
            sum = sum * (-1.0);

            if (Double.IsNaN(sum))
            {
                Report.Line("NaN value occured");
            }
            return sum;
        }
    }

    public class Node
    {
        public bool isLeaf = false;
        public int DistanceFromRoot = 0;
        public Node LeftChild;
        public Node RightChild;
        public Decider Decider;
        public ClassDistribution ClassDistribution;
        public int GlobalIndex = -1;    //this node's global index in the forest 

        public void getClassDecisionRecursive(DataPoint dataPoint, List<TextonNode> currentList, TrainingParams parameters)
        {
            switch(parameters.ClassificationMode)
            {
                case ClassificationMode.Semantic:

                    var rt = new TextonNode();
                    rt.Index = GlobalIndex;
                    rt.Level = DistanceFromRoot;
                    rt.Value = 1;
                    currentList.Add(rt);

                    //descend left or right, or return if leaf
                    if (!this.isLeaf)
                    {
                        bool leftright = Decider.Decide(dataPoint);
                        if (leftright)   //true means left
                        {
                            LeftChild.getClassDecisionRecursive(dataPoint, currentList, parameters);
                        }
                        else            //false means right
                        {
                            RightChild.getClassDecisionRecursive(dataPoint, currentList, parameters);
                        }
                    }
                    else //break condition
                    {
                        return;
                    }
                    return;
                case ClassificationMode.LeafOnly:

                    if(!this.isLeaf) //we are in a branching point, continue forward
                    {
                        bool leftright = Decider.Decide(dataPoint);
                        if(leftright)   //true means left
                        {
                            LeftChild.getClassDecisionRecursive(dataPoint, currentList, parameters);
                        }
                        else            //false means right
                        {
                            RightChild.getClassDecisionRecursive(dataPoint, currentList, parameters);
                        }
                    }
                    else            //we are at a leaf, take this class distribution as result
                    {
                        var result = new TextonNode();
                        result.Index = GlobalIndex;
                        result.Level = DistanceFromRoot;
                        result.Value = 1;
                        var resList = new List<TextonNode>();
                        resList.Add(result);
                        return;
                    }
                    return;
                    
                default:
                    return;
            }
        }

        //every node adds 0 to the histogram (=initialize the histogram parameters)
        public void initializeEmpty(List<TextonNode> currentList)
        {
            var rt = new TextonNode();
            rt.Index = GlobalIndex;
            rt.Level = DistanceFromRoot;
            rt.Value = 0;
            currentList.Add(rt);

            //descend left or right, or return if leaf
            if (!this.isLeaf)
            {
                LeftChild.initializeEmpty(currentList);
                RightChild.initializeEmpty(currentList);
            }
        }
    }

    public class Tree
    {
        public Node Root;
        public int Index = -1;   //this tree's index within the forest, is set by the forest during initialization
        public int NumNodes = 0;    //how many nodes does this tree have in total

        public Tree()
        {
            Root = new Node();
            Root.GlobalIndex = this.Index;
        }

        public List<TextonNode> getClassDecision(DataPointSet dp, TrainingParams parameters)
        {
            var result = new List<TextonNode>();

            foreach(var point in dp.DPSet)
            {
                var cumulativeList = new List<TextonNode>();
                Root.getClassDecisionRecursive(point, cumulativeList, parameters);
                foreach (var el in cumulativeList)        //this is redundant with initializeEmpty -> todo
                {
                    el.TreeIndex = this.Index;
                }
                result.AddRange(cumulativeList);
            }

            return result;
        }

        public void initializeEmpty(List<TextonNode> currentList)
        {
            var cumulativeList = new List<TextonNode>();
            Root.initializeEmpty(cumulativeList);
            foreach (var el in cumulativeList)
            {
                el.TreeIndex = this.Index;
            }
            currentList.AddRange(cumulativeList);
        }
    }
    
    public class Forest
    {
        public Tree[] Trees;
        public string Name { get; }
        public int NumTrees { get; }

        public int numNodes = -1;

        public Forest() { }
        
        public Forest(string name, int numberOfTrees)
        {
            Name = name;
            NumTrees = numberOfTrees;
            InitializeEmptyForest();
        }

        private void InitializeEmptyForest()
        {
            Trees = new Tree[NumTrees].SetByIndex(i => new Tree() { Index = i });
        }

        public Textonization GetTextonRepresentation(ImagePatch img, TrainingParams parameters)
        {
            if(numNodes <= -1)  //this part is deprecated
            {
                numNodes = Trees.Sum(x=>x.NumNodes);
            }

            //we must use the sampling provider of a tree because parameters are currently not saved to file -> fix this!
            var imageSamples = Trees[0].Root.Decider.SamplingProvider.getDataPoints(img);

            var result = new Textonization();
            result.initializeEmpty(numNodes);

            var basicNodes = new List<TextonNode>();

            Algo.treeCounter = 0;

            foreach(var tree in Trees)    //for each tree, get a textonization of the data set and sum up the result
            {
                Algo.treeCounter++;

                tree.initializeEmpty(basicNodes);

                var curTex = tree.getClassDecision(imageSamples, parameters);

                result.addNodes(curTex);

            }

            result.addNodes(basicNodes);    //we can add all empty nodes after calculation because it simply increments all nodes by 0 (no change) while initializing unrepresented nodes

            return result;
        }
    }

    #endregion

    #region Class Labels and Distributions

    /// <summary>
    /// Category/class/label.
    /// </summary>
    public class ClassLabel
    {
        //index in the global label list
        public int Index { get; }
        //string identifier
        public string Name { get; }

        public ClassLabel()
        {
            Index = -1;
            Name = "";
        }

        public ClassLabel(int index, string name)
        {
            Index = index;
            Name = name;
        }
    }

    //a class distribution, containing a histogram over all classes and their respective values.
    public class ClassDistribution
    {
        //the histogram value for each label
        public double[] ClassValues;
        
        //number of labels (if variable - this is specified in the paper but not used)
        public int Length;

        //dont use this constructor, JSON only
        public ClassDistribution()
        {

        }

        //adds two class distributions, requires them to use the same global class label list
        public static ClassDistribution operator+(ClassDistribution a, ClassDistribution b)
        {
            ClassDistribution result = new ClassDistribution(GlobalParams.Labels);

            foreach (var cl in GlobalParams.Labels)
            {
                result.addClNum(cl, a.ClassValues[cl.Index] + b.ClassValues[cl.Index]);
            }

            return result;
        }

        //multiply histogram values with a constant
        public static ClassDistribution operator*(ClassDistribution a, double b)
        {
            ClassDistribution result = new ClassDistribution(GlobalParams.Labels);

            foreach (var cl in GlobalParams.Labels)
            {
                result.addClNum(cl, a.ClassValues[cl.Index] * b);
            }

            return result;
        }

        //initializes all classes with a count of 0
        public ClassDistribution(ClassLabel[] allLabels)
        {
            ClassValues = new double[allLabels.Length]; ;
            for(int i=0;i<allLabels.Length;i++) //allLabels must have a sequence of indices [0-n]
            {
                ClassValues[i] = 0;
            }
            Length = allLabels.Length;
        }

        //initialize classes and add the data points
        public ClassDistribution(ClassLabel[] allLabels, DataPointSet dps)
            : this(allLabels)
        {
            addDatapoints(dps);
        }

        //add one data point to histogram
        public void addDP(DataPoint dp)
        {
            if(dp.label == -2)
            {
                return;
            }

            double incrementValue = 1.0d;

            addClNum(GlobalParams.Labels.Where(x => x.Index == dp.label).First() , incrementValue);
        }

        //add one histogram entry
        public void addClNum(ClassLabel cl, double num)
        {
            ClassValues[cl.Index] = ClassValues[cl.Index] + num;
        }

        //add all data points to histogram
        public void addDatapoints(DataPointSet dps)
        {
            foreach (var dp in dps.DPSet)
            {
                this.addDP(dp);
            }
        }

        //returns the proportion of the elements of this class to the number of all elements in this distribution
        public double getClassProbability(ClassLabel label)  
        {
            var sum = ClassValues.Sum();

            if(sum == 0)
            {
                return 0;
            }

            var prob = ClassValues[label.Index] / sum;

            return prob;
        }

        //returns sum of histogram values
        public double getClassDistSum()
        {
            return ClassValues.Sum();
        }

        //normalize histogram
        public void normalize()
        {
            var sum = ClassValues.Sum();

            if (sum == 0)
            {
                return;
            }

            for(int i=0; i<ClassValues.Length;i++)
            {
                ClassValues[i] = ClassValues[i] / sum;
            }
        }
    }
    #endregion

    #region Images and I/O

    //the textonized form of a pixel region as returned by a STForest
    public class Textonization
    {
        public double[] Values; //old format - to be removed
        public TextonNode[] Nodes;  //new format
        public int Length;

        public Textonization()
        {

        }

        public void initializeEmpty(int numNodes)
        {
            Length = numNodes;
            Nodes = new TextonNode[numNodes];

            for (int i = 0; i < numNodes; i++)
            {
                Nodes[i] = new TextonNode() { Index = i, Level = 0, Value = 0 };
            }
        }

        public void addValues(double[] featureValues)
        {
            this.Values = featureValues;
            this.Length = featureValues.Length;
        }

        public void setNodes(TextonNode[] featureNodes)
        {
            this.Nodes = featureNodes;
            this.Length = featureNodes.Length;
        }

        public void addNodes(List<TextonNode> featureNodes)
        {
            foreach(var node in featureNodes)
            {
                var localNode = this.Nodes[node.Index];

                localNode.Level = node.Level;
                localNode.TreeIndex = node.TreeIndex;
                localNode.Value += node.Value;
            }
        }

        public static Textonization operator+(Textonization current, Textonization other)     //adds two textonizations. must have same length and same node indices (=be from the same forest)
        {
            var result = new Textonization();

            result.Length = current.Length;

            for (int i = 0; i < current.Length; i++)
            {
                var curNode = current.Nodes[i];
                var otherNode = other.Nodes.First(t => t.Index == curNode.Index);

                var res = new TextonNode();
                res.Index = curNode.Index;
                res.Level = curNode.Level;
                res.Value = curNode.Value + otherNode.Value;
                result.Nodes[i] = res;
            }

            return result;
        }

    }

    public class TextonNode
    {
        public int Index = -1; //the tree node's global identifier
        public int TreeIndex = -1; //the index of the tree this node belongs to
        public int Level = -1; //the level of this node in the tree
        public double Value = 0;   //"histogram" value

    }

    //wrapper class for PixImage
    public class ImagePatch
    {
        public string ImagePath;

        //image coordinates of the rectangle this patch represents
        //top left pixel
        public int X = -1;
        public int Y = -1;
        //rectangle size in pixels
        public int SX = -1;
        public int SY = -1;

        //the image will be loaded into memory on first use
        private PixImage<byte> pImage;
        private bool isLoaded = false;

        //don't use, JSON only
        public ImagePatch()
        {

        }

        //Creates a new image without loading it into memory
        public ImagePatch(string filePath)
        {
            ImagePath = filePath;
            X = 0;
            Y = 0;
            SX = int.MaxValue;
            SY = int.MaxValue;
        }

        public ImagePatch(string filePath, int X, int Y, int SX, int SY)
        {
            ImagePath = filePath;
            this.X = X;
            this.Y = Y;
            this.SX = SX;
            this.SY = SY;
        }

        [JsonIgnore]
        public PixImage<byte> PixImage
        {
            get
            {
                if (!isLoaded)
                {
                    Load();
                }
                return pImage;
            }
        }
        
        private void Load()
        {
            //makes it so pImage is the specified (X,Y,SX,SY) subrectangle of the PixImage

            pImage = new PixImage<byte>(ImagePath);

            int actualSizeX = Math.Min(SX, pImage.Size.X);
            int actualSizeY = Math.Min(SY, pImage.Size.Y);

            var pVol = pImage.Volume;
            var subVol = pVol.SubVolume(new V3i(X,Y,0), new V3i(actualSizeX, actualSizeY, 3));

            var newImageVol = subVol.ToImage();

            var newImage = new PixImage<byte>(Col.Format.RGB, newImageVol);

            pImage = newImage;

            isLoaded = true;
        }
    }

    /// <summary>
    /// STImage with added class label, used for training and testing.
    /// </summary>
    public class LabeledImage
    {
        public ImagePatch Patch { get; }
        public ClassLabel ClassLabel { get; }

        //this value can be changed if needed different image bias during training
        public double TrainingBias = 1.0f;

        //don't use, JSON only
        public LabeledImage() { }

        //creates a new image from filename
        public LabeledImage(string imageFilename, ClassLabel label)
        {
            Patch = new ImagePatch(imageFilename);
            ClassLabel = label;
        }
    }

    //STLabelledImage with added Textonization
    public class TextonizedLabelledImage
    {
        public LabeledImage Image { get; }
        public Textonization Textonization { get; }

        public ClassLabel Label => Image.ClassLabel;

        //don't use, JSON only
        public TextonizedLabelledImage() { }

        //copy constructor
        public TextonizedLabelledImage(LabeledImage image, Textonization textonization)
        {
            Image = image;
            Textonization = textonization;
        }
    }
#endregion

    #region Parameter Classes

    public class TrainingParams
    {
        public TrainingParams()
        {
        }

        public TrainingParams(int treeCount, int maxTreeDepth,
            int trainingSubsetCountPerTree, int trainingImageSamplingWindow,
            ClassLabel[] labels,
            int maxFeatureCount = 999999999,
            FeatureType featureType = FeatureType.SelectRandom
            )
        {
            this.FeatureProviderFactory = new FeatureProviderFactory();
            this.FeatureProviderFactory.selectProvider(featureType, trainingImageSamplingWindow);
            this.SamplingProviderFactory = new SamplingProviderFactory();
            this.SamplingProviderFactory.selectProvider(this.SamplingType, trainingImageSamplingWindow);
            this.TreesCount = treeCount;
            this.MaxTreeDepth = maxTreeDepth;
            this.ImageSubsetCount = trainingSubsetCountPerTree;
            this.SamplingWindow = trainingImageSamplingWindow;
            this.MaxSampleCount = maxFeatureCount;
            this.FeatureType = featureType;
            this.Labels = labels;
        }

        public string ForestName = "new forest";       //identifier of the forest, has no usage except for readability if saving to file
        public int ClassesCount = GlobalParams.Labels.Max(x => x.Index) + 1;        //how many classes
        public int TreesCount;          //how many trees should the forest have
        public int MaxTreeDepth;        //maximum depth of one tree
        public int ImageSubsetCount;    //how many images should be randomly selected from the training set for each tree's training
        public int SamplingWindow;      //side length of the square window around a pixel to be sampled; half of this size is effectively the border around the image
        public int MaxSampleCount;      //limit the maximum number of samples for all images (selected randomly from all samples) -> set this to 99999999 for all samples
        public FeatureType FeatureType; //the type of feature that should be extracted using the feature providers
        public SamplingType SamplingType = SamplingType.RegularGrid;//mode of sampling
        public int RandomSamplingCount = 500;  //if sampling = random sampling, how many points?
        public FeatureProviderFactory FeatureProviderFactory;       //creates a new feature provider for each decision node in the trees to apply to a sample point (window); currently value of a random pixel, sum of two random pixels, absolute difference of two random pixels
        public SamplingProviderFactory SamplingProviderFactory;     //creates a new sample point provider which is currently applied to all pictures; currently sample a regular grid with stride, sample a number of random points
        public int ThresholdCandidateNumber = 16;    //how many random thresholds should be tested in a tree node to find the best one
        public double ThresholdInformationGainMinimum = 0.01d;    //break the tree node splitting if no threshold has a score better than this
        public ClassificationMode ClassificationMode = ClassificationMode.Semantic;    //what feature representation method to use; currently: standard representation by leaves only, semantic texton representation using the entire tree
        public bool ForcePassthrough = false;   //during forest generation, force each datapoint to reach a leaf (usually bad)
        public bool EnableGridSearch = false;         //the SVM tries out many values to find the optimal C (can take a long time)

        //todo: definitely parse this from a text file or so

        public ClassLabel[] Labels;
}


    public static class GlobalParams
    {
        //experimental/temporary helper params
        public static bool EnableSampleNumberCountUnbias = false;    //remove bias for number of samples per image, enable if images vary in size for regular grid sampling
        public static bool NormalizeDistributions = false;           //normalize class distributions to [0-1]

        //required params
        public static ClassLabel[] Labels;     //list of class labels that is used globally
    }

    public class FilePaths
    {
        public string WorkDir;
        public string forestFilePath;
        public string testsetpath1;
        public string testsetpath2;
        public string semantictestsetpath1;
        public string semantictestsetpath2;
        public string trainingsetpath;
        public string kernelsetpath;
        public string trainingTextonsFilePath;
        public string testTextonsFilePath;
    }

#endregion
}
