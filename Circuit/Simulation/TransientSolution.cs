﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Reflection;
using SyMath;

namespace Circuit
{
    /// <summary>
    /// Represents the solutions of a system of equations derived from a Circuit for transient analysis.
    /// </summary>
    public class TransientSolution
    {
        // Expression for t at the previous timestep.
        public static readonly Variable t0 = Component.t0;
        // And the current timestep.
        public static readonly Variable t = Component.t;

        private Quantity h;
        /// <summary>
        /// The length of a timestep given by this solution.
        /// </summary>
        public Quantity TimeStep { get { return h; } }
        
        private List<SolutionSet> solutions;
        /// <summary>
        /// Ordered list of SolutionSet objects that describe the overall solution. If SolutionSet
        /// a follows SolutionSet b in this enumeration, b's solution may depend on a's solutions.
        /// </summary>
        public IEnumerable<SolutionSet> Solutions { get { return solutions; } }

        private List<Arrow> initialConditions;
        /// <summary>
        /// Set of expressions describing the initial conditions of the variables in this solution.
        /// </summary>
        public IEnumerable<Arrow> InitialConditions { get { return initialConditions; } }

        public TransientSolution(
            Quantity TimeStep,
            IEnumerable<SolutionSet> Solutions,
            IEnumerable<Arrow> InitialConditions)
        {
            h = TimeStep;
            solutions = Solutions.ToList();
            initialConditions = InitialConditions.ToList();
        }

        /// <summary>
        /// Check if any of the SolutionSets in this solution depend on x.
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        public bool DependsOn(Expression x) { return solutions.Any(i => i.DependsOn(x)); }

        /// <summary>
        /// Solve the circuit for transient simulation.
        /// </summary>
        /// <param name="Analysis">Analysis from the circuit to solve.</param>
        /// <param name="TimeStep">Discretization timestep.</param>
        /// <param name="Log">Where to send output.</param>
        /// <returns>TransientSolution describing the solution of the circuit.</returns>
        public static TransientSolution Solve(Analysis Analysis, Quantity TimeStep, bool InitialConditions, ILog Log)
        {
            Timer time = new Timer();

            Expression h = TimeStep;

            Log.WriteLine(MessageType.Info, "Building solution for h={0}", TimeStep.ToString());
            
            // Analyze the circuit to get the MNA system and unknowns.
            List<Equal> mna = Analysis.Equations.ToList();
            List<Expression> y = Analysis.Unknowns.ToList();
            LogExpressions(Log, MessageType.Verbose, "System of " + mna.Count + " equations and " + y.Count + " unknowns = {{ " + y.UnSplit(", ") + " }}", mna);
            
            // Find out what variables have differential relationships.
            List<Expression> dy_dt = y.Where(i => mna.Any(j => j.DependsOn(D(i, t)))).Select(i => D(i, t)).ToList();

            // Find steady state solution for initial conditions.
            List<Arrow> initial = new List<Arrow>();
            if (InitialConditions)
            {
                Log.WriteLine(MessageType.Info, "[{0}] Performing steady state analysis...", time);
                List<Equal> dc = mna
                    // Derivatives are zero in the steady state.
                    .Evaluate(dy_dt.Select(i => Arrow.New(i, 0)))
                    // t = 0 and t0 = 0
                    .Evaluate(Arrow.New(t, 0), Arrow.New(t0, 0))
                    // Use the initial conditions from MNA.
                    .Evaluate(Analysis.InitialConditions)
                    .OfType<Equal>().ToList();
                try
                {
                    initial = dc.NSolve(y.Select(i => Arrow.New(i.Evaluate(t, 0), 0)));
                    LogExpressions(Log, MessageType.Verbose, "Initial conditions:", initial);
                }
                catch (AlgebraException)
                {
                    Log.WriteLine(MessageType.Warning, "Failed to find steady state for initial conditions, circuit may be unstable.");
                }
            }
            else
            {
                Log.WriteLine(MessageType.Info, "Skipping steady state analysis.");
            }
            
            // Transient analysis of the system.
            Log.WriteLine(MessageType.Info, "[{0}] Performing transient analysis...", time);

            // Separate mna into differential and algebraic equations.
            List<LinearCombination> diffeq = mna.Where(i => i.DependsOn(dy_dt)).InTermsOf(dy_dt).ToList();
            mna = mna.Where(i => !i.DependsOn(dy_dt)).ToList();
            LogList(Log, MessageType.Verbose, "Differential equations:", diffeq.Select(i => i.ToString()));

            // Solve the diff eq for dy/dt and integrate the results.
            diffeq.RowReduce(dy_dt);
            diffeq.BackSubstitute(dy_dt);
            List<Equal> integrated = SolveAndRemove(diffeq, dy_dt)
                .NDIntegrate(t, t0, h, IntegrationMethod.Trapezoid)
                .Select(i => Equal.New(i.Left, i.Right)).ToList();
            mna.AddRange(integrated);
            LogExpressions(Log, MessageType.Verbose, "Integrated solutions:", integrated);

            // The remaining equations from the system of diff eqs should be algebraic.
            mna.AddRange(diffeq.Select(i => Equal.New(i.ToExpression(), 0)));
            LogExpressions(Log, MessageType.Verbose, "Discretized system:", mna);

            // Solving the system...
            List<SolutionSet> solutions = new List<SolutionSet>();

            // Find linear solutions for y and substitute them into the system. Linear circuits should be completely solved here.
            List<Arrow> linear = mna.Solve(y);
            linear.RemoveAll(i => i.Right.DependsOn(y));
            mna = mna.Evaluate(linear).OfType<Equal>().ToList();
            y.RemoveAll(i => linear.Any(j => j.Left.Equals(i)));
            if (linear.Any())
            {
                // Factor the solution to minimize arithmetic.
                Factor(linear);

                solutions.Add(new LinearSolutions(linear));
                LogExpressions(Log, MessageType.Verbose, "Linear solutions:", linear);
            }

            // If there are any variables left, there are some non-linear equations requiring numerical techniques to solve.
            if (y.Any())
            {
                // The variables of this system are the newton iteration updates.
                List<Expression> dy = y.Select(i => NewtonIteration.Delta(i)).ToList();

                // Rearrange the MNA system to be F[y] == 0.
                List<Expression> F = mna.Select(i => i.Left - i.Right).ToList();
                // Compute JxF*dy + F(y0) == 0.
                List<LinearCombination> J = F.Jacobian(y.Select(i => Arrow.New(i, NewtonIteration.Delta(i))));
                foreach (LinearCombination i in J)
                    i[1] = ((Expression)i.Tag);

                // ly is the subset of y that can be found linearly.
                List<Expression> ly = dy.Where(j => !J.Any(i => i[j].DependsOn(NewtonIteration.DeltaOf(j)))).ToList();
                // Swap the columns such that ly appear first.
                dy = dy.Except(ly).ToList();
                foreach (LinearCombination i in J)
                    i.SwapColumns(ly.Concat(dy));
                // If there is only one variable to be solved for, just do it now.
                if (dy.Count == 1)
                {
                    ly.Add(dy[0]);
                    dy.Clear();
                }

                // Compute the row-echelon form of the linear part of the Jacobian.
                J.RowReduce(ly);
                // Solutions for each linear update equation.
                List<Arrow> solved = SolveAndRemove(J, ly);

                // Initial guess for y(t) = y(t0).
                List<Arrow> guess = y.Select(i => Arrow.New(i, i.Evaluate(t, t0))).ToList();

                // Factor the solution to minimize arithmetic.
                Factor(solved);
                foreach (LinearCombination i in J)
                    Factor(i);
                
                solutions.Add(new NewtonIteration(solved, J, dy, guess));
                LogList(Log, MessageType.Verbose, String.Format("Non-linear Newton's method updates ({0}):", dy.UnSplit(", ")), J.Select(i => i.ToString() + " == 0"));
                LogExpressions(Log, MessageType.Verbose, "Linear Newton's method updates:", solved);
            }

            Log.WriteLine(MessageType.Info, "[{0}] System solved, {1} solution sets for {2} unknowns.", 
                time, 
                solutions.Count, 
                solutions.Sum(i => i.Unknowns.Count()));
            
            return new TransientSolution(
                h,
                solutions,
                initial);
        }
        public static TransientSolution Solve(Analysis Analysis, Quantity TimeStep, ILog Log)
        {
            return Solve(Analysis, TimeStep, true, Log);
        }

        // Solve S for x, removing the rows of S that are used for a solution.
        private static List<Arrow> SolveAndRemove(IList<LinearCombination> S, IEnumerable<Expression> x)
        {
            // Solve for the variables in x.
            List<Arrow> result = new List<Arrow>();
            foreach (Expression j in x.Reverse())
            {
                // Find the row with the pivot variable in this position.
                LinearCombination i = S.FindPivot(j);

                // If there is no pivot in this position, find any row with a non-zero coefficient of j.
                if (i == null)
                    i = S.FirstOrDefault(s => !s[j].EqualsZero());

                // Solve the row for i.
                if (i != null)
                {
                    result.Add(Arrow.New(j, i.Solve(j)));
                    S.Remove(i);
                }
            }
            return result;
        }

        private static void Factor(List<Arrow> x)
        {
            for (int i = 0; i < x.Count; ++i)
                x[i] = Arrow.New(x[i].Left, x[i].Right.Factor());
        }

        private static void Factor(LinearCombination x)
        {
            foreach (Expression i in x.Basis.Append(1))
                x[i] = x[i].Factor();
        }
        
        // Filters the solutions in S that are dependent on x if evaluated in order.
        private static IEnumerable<Arrow> IndependentSolutions(IEnumerable<Arrow> S, IEnumerable<Expression> x)
        {
            foreach (Arrow i in S)
            {
                if (!i.Right.DependsOn(x))
                {
                    yield return i;
                    x = x.Except(i.Left);
                }
            }
        }

        // Shorthand for df/dx.
        protected static Expression D(Expression f, Expression x) { return Call.D(f, x); }

        // Check if x is a derivative
        protected static bool IsD(Expression f, Expression x)
        {
            Call C = f as Call;
            if (!ReferenceEquals(C, null))
                return C.Target.Name == "D" && C.Arguments[1].Equals(x);
            return false;
        }

        // Logging helpers.
        private static void LogList(ILog Log, MessageType Type, string Title, IEnumerable<string> List)
        {
            if (List.Any())
            {
                Log.WriteLine(Type, Title);
                foreach (string i in List)
                    Log.WriteLine(Type, "  " + i);
                Log.WriteLine(Type, "");
            }
        }

        private static void LogExpressions(ILog Log, MessageType Type, string Title, IEnumerable<Expression> Expressions)
        {
            LogList(Log, Type, Title, Expressions.Select(i => i.ToString()));
        }
    }
}
