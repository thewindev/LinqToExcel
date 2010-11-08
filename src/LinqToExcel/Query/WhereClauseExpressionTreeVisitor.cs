﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Remotion.Data.Linq.Parsing;
using System.Data.OleDb;
using System.Linq.Expressions;
using LinqToExcel.Extensions;
using Remotion.Data.Linq.Clauses.Expressions;

namespace LinqToExcel.Query
{
    public class WhereClauseExpressionTreeVisitor : ThrowingExpressionTreeVisitor
    {
        private readonly StringBuilder _whereClause = new StringBuilder();
        private readonly List<OleDbParameter> _params = new List<OleDbParameter>();
        private readonly Dictionary<string, string> _columnMapping;
        private readonly List<string> _columnNamesUsed = new List<string>();
        private readonly Type _sheetType;

        public WhereClauseExpressionTreeVisitor(Type sheetType, Dictionary<string, string> columnMapping)
        {
            _sheetType = sheetType;
            _columnMapping = columnMapping;
        }

        public string WhereClause
        {
            get { return _whereClause.ToString(); }
        }

        public IEnumerable<OleDbParameter> Params
        {
            get { return _params; }
        }

        public IEnumerable<string> ColumnNamesUsed
        {
            get { return _columnNamesUsed.Select(x => x.Replace("[", "").Replace("]", "")); }
        }

        public void Visit(Expression expression)
        {
            base.VisitExpression(expression);
        }

        protected override Exception CreateUnhandledItemException<T>(T unhandledItem, string visitMethod)
        {
            throw new NotImplementedException(visitMethod + " method is not implemented");
        }

        protected override Expression VisitBinaryExpression(BinaryExpression bExp)
        {
            _whereClause.Append("(");

            // Patch for vb.net expression that are always considered a MethodCallExpression even if they are not.
            // see http://www.re-motion.org/blogs/mix/archive/2009/10/16/vb.net-specific-text-comparison-in-linq-queries.aspx
            bExp = ConvertVbStringCompare(bExp);

            //We always want the MemberAccess (ColumnName) to be on the left side of the statement
            var bLeft = bExp.Left;
            var bRight = bExp.Right;
            if ((bExp.Right.NodeType == ExpressionType.MemberAccess) &&
                (((MemberExpression)bExp.Right).Member.DeclaringType == _sheetType))
            {
                bLeft = bExp.Right;
                bRight = bExp.Left;
            }

            VisitExpression(bLeft);
            switch (bExp.NodeType)
            {
                case ExpressionType.AndAlso:
                    _whereClause.Append(" AND ");
                    break;
                case ExpressionType.Equal:
                    if (bRight.IsNullValue())
                        _whereClause.Append(" IS NULL");
                    else
                        _whereClause.Append(" = ");
                    break;
                case ExpressionType.GreaterThan:
                    _whereClause.Append(" > ");
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    _whereClause.Append(" >= ");
                    break;
                case ExpressionType.LessThan:
                    _whereClause.Append(" < ");
                    break;
                case ExpressionType.LessThanOrEqual:
                    _whereClause.Append(" <= ");
                    break;
                case ExpressionType.NotEqual:
                    if (bRight.IsNullValue())
                        _whereClause.Append(" IS NOT NULL");
                    else
                        _whereClause.Append(" <> ");
                    break;
                case ExpressionType.OrElse:
                    _whereClause.Append(" OR ");
                    break;
                default:
                    throw new NotSupportedException(string.Format("{0} statement is not supported", bExp.NodeType.ToString()));
            }
            if (!bRight.IsNullValue())
                VisitExpression(bRight);
            _whereClause.Append(")");
            return bExp;
        }

        protected BinaryExpression ConvertVbStringCompare(BinaryExpression exp)
        {
            if (exp.Left.NodeType == ExpressionType.Call)
            {
                var compareStringCall = (MethodCallExpression)exp.Left;
                if (compareStringCall.Method.DeclaringType.FullName == "Microsoft.VisualBasic.CompilerServices.Operators" && compareStringCall.Method.Name == "CompareString")
                {
                    var arg1 = compareStringCall.Arguments[0];
                    var arg2 = compareStringCall.Arguments[1];

                    switch (exp.NodeType)
                    {
                        case ExpressionType.LessThan:
                            return Expression.LessThan(arg1, arg2);
                        case ExpressionType.LessThanOrEqual:
                            return Expression.LessThanOrEqual(arg1, arg2);
                        case ExpressionType.GreaterThan:
                            return Expression.GreaterThan(arg1, arg2);
                        case ExpressionType.GreaterThanOrEqual:
                            return Expression.GreaterThanOrEqual(arg1, arg2);
                        case ExpressionType.NotEqual:
                            return Expression.NotEqual(arg1, arg2);
                        default:
                            return Expression.Equal(arg1, arg2);
                    }
                }
            }
            return exp;
        }

        protected override Expression VisitMemberExpression(MemberExpression mExp)
        {
            //Set the column name to the property mapping if there is one, 
            //else use the property name for the column name
            var columnName = (_columnMapping.ContainsKey(mExp.Member.Name)) ? 
                _columnMapping[mExp.Member.Name] : 
                mExp.Member.Name;
            _whereClause.AppendFormat("[{0}]", columnName);
            _columnNamesUsed.Add(columnName);
            return mExp;
        }

        protected override Expression VisitConstantExpression(ConstantExpression cExp)
        {
            _params.Add(new OleDbParameter("?", cExp.Value));
            _whereClause.Append("?");
            return cExp;
        }

        /// <summary>
        /// This method is visited when the LinqToExcel.Row type is used in the query
        /// </summary>
        protected override Expression VisitUnaryExpression(UnaryExpression uExp)
        {
            var columnName = GetColumnName(uExp.Operand);
            _whereClause.Append(columnName);
            return uExp;
        }

        /// <summary>
        /// Only As<>() method calls on the LinqToExcel.Row type are support
        /// </summary>
        protected override Expression VisitMethodCallExpression(MethodCallExpression mExp)
        {
            if (mExp.Method.Name == "Contains")
            {
                _whereClause.Append("(");
                VisitExpression(mExp.Object);
                _whereClause.Append(" LIKE ?)");

                var value = mExp.Arguments.First().ToString().Replace("\"", "");
                var parameter = string.Format("%{0}%", value);
                _params.Add(new OleDbParameter("?", parameter));
            }
            else if (mExp.Method.Name == "StartsWith")
            {
                _whereClause.Append("(");
                VisitExpression(mExp.Object);
                _whereClause.Append(" LIKE ?)");

                var value = mExp.Arguments.First().ToString().Replace("\"", "");
                var parameter = string.Format("{0}%", value);
                _params.Add(new OleDbParameter("?", parameter));
            }
            else if (mExp.Method.Name == "EndsWith")
            {
                _whereClause.Append("(");
                VisitExpression(mExp.Object);
                _whereClause.Append(" LIKE ?)");

                var value = mExp.Arguments.First().ToString().Replace("\"", "");
                var parameter = string.Format("%{0}", value);
                _params.Add(new OleDbParameter("?", parameter));
            }
            else
            {
                var columnName = GetColumnName(mExp);
                _whereClause.Append(columnName);
                _columnNamesUsed.Add(columnName);
            }
            return mExp;
        }

        /// <summary>
        /// Retrieves the column name from a member expression or method call expression
        /// </summary>
        /// <param name="exp">Expression</param>
        private string GetColumnName(Expression exp)
        {
            if (exp is MemberExpression)
                return GetColumnName((MemberExpression)exp);
            else
                return GetColumnName((MethodCallExpression)exp);
        }

        /// <summary>
        /// Retrieves the column name from a member expression
        /// </summary>
        /// <param name="mExp">Member Expression</param>
        private string GetColumnName(MemberExpression mExp)
        {
            return "[" + mExp.Member.Name + "]";
        }

        /// <summary>
        /// Retrieves the column name from a method call expression
        /// </summary>
        /// <param name="exp">Method Call Expression</param>
        private string GetColumnName(MethodCallExpression mExp)
        {
            MethodCallExpression method = mExp;
            if (mExp.Object is MethodCallExpression)
                method = (MethodCallExpression)mExp.Object;

            var arg = method.Arguments.First();
            if (arg.Type == typeof(int))
            {
                if (_sheetType == typeof(RowNoHeader))
                    return string.Format("F{0}", Int32.Parse(arg.ToString()) + 1);
                else
                    throw new ArgumentException("Can only use column indexes in WHERE clause when using WorksheetNoHeader");
            }

            var columnName = arg.ToString().ToCharArray();
            columnName[0] = "[".ToCharArray().First();
            columnName[columnName.Length - 1] = "]".ToCharArray().First();
            return new string(columnName);
        }
    }
}
