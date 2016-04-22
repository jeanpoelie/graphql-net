﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using GraphQL.Net.SchemaAdapters;

namespace GraphQL.Net
{
    public abstract class GraphQLSchema
    {
        internal readonly VariableTypes VariableTypes = new VariableTypes();
        internal abstract GraphQLType GetGQLType(Type type);
    }

    public class GraphQLSchema<TContext> : GraphQLSchema
    {
        internal readonly Func<TContext> ContextCreator;
        private readonly List<GraphQLType> _types = new List<GraphQLType>();
        private readonly List<GraphQLQueryBase<TContext>> _queries = new List<GraphQLQueryBase<TContext>>();
        internal bool Completed;

        public static readonly ParameterExpression DbParam = Expression.Parameter(typeof (TContext), "db");

        public GraphQLSchema(Func<TContext> contextCreator)
        {
            ContextCreator = contextCreator;
            AddDefaultPrimitives();
        }

        public void AddString<T>(Func<string, T> translate, string name = null)
            => VariableTypes.AddType(CustomVariableType.String(translate, name));

        public void AddInteger<T>(Func<long, T> translate, string name = null)
            => VariableTypes.AddType(CustomVariableType.Integer(translate, name));

        public void AddFloat<T>(Func<double, T> translate, string name = null)
            => VariableTypes.AddType(CustomVariableType.Float(translate, name));

        public void AddBoolean<T>(Func<bool, T> translate, string name = null)
            => VariableTypes.AddType(CustomVariableType.Boolean(translate, name));

        private void AddDefaultPrimitives()
        {
            AddString(Guid.Parse);
            AddFloat(d => (float)d, "Float32");
            AddInteger(i => (int)i, "Int");
        }

        public GraphQLTypeBuilder<TContext, TEntity> AddType<TEntity>(string name = null, string description = null)
        {
            var type = typeof (TEntity);
            if (_types.Any(t => t.CLRType == type))
                throw new ArgumentException("Type has already been added");

            var gqlType = new GraphQLType(type) {IsScalar = type.IsPrimitive, Description = description ?? ""};
            if (!string.IsNullOrEmpty(name))
                gqlType.Name = name;
            _types.Add(gqlType);

            return new GraphQLTypeBuilder<TContext, TEntity>(this, gqlType);
        }

        public GraphQLTypeBuilder<TContext, TEntity> GetType<TEntity>()
        {
            var type = _types.FirstOrDefault(t => t.CLRType == typeof (TEntity));
            if (type == null)
                throw new KeyNotFoundException($"Type {typeof(TEntity).FullName} could not be found.");

            return new GraphQLTypeBuilder<TContext, TEntity>(this, type);
        }

        internal Schema<TContext> Adapter { get; private set; }

        public void Complete()
        {
            if (Completed)
                throw new InvalidOperationException("Schema has already been completed.");

            AddDefaultTypes();

            foreach (var type in _types.Where(t => t.QueryType == null))
                CompleteType(type);

            Adapter = new Schema<TContext>(this);
            Completed = true;
        }

        private static void CompleteType(GraphQLType type)
        {
            // validation maybe perform somewhere else
            if (type.IsScalar && type.Fields.Count != 0)
                throw new Exception("Scalar types must not have any fields defined."); // TODO: Schema validation exception?
            if (!type.IsScalar && type.Fields.Count == 0)
                throw new Exception("Non-scalar types must have at least one field defined."); // TODO: Schema validation exception?

            if (type.IsScalar)
            {
                type.QueryType = type.CLRType;
                return;
            }

            var fieldDict = type.Fields.Where(f => !f.IsPost).ToDictionary(f => f.Name, f => f.Type.IsScalar ? f.Type.CLRType : typeof (object));
            type.QueryType = DynamicTypeBuilder.CreateDynamicType(type.Name + Guid.NewGuid(), fieldDict);
        }

        private void AddDefaultTypes()
        {
            var schemaType = AddType<GraphQLSchema<TContext>>("__Schema");
            schemaType.AddField("types", (db, s) => s.Types.Concat(VariableTypes.IntrospectionTypes).ToList());
            schemaType.AddField("queryType", (db, s) => (GraphQLType) null); // TODO: queryType
            schemaType.AddField("mutationType", (db, s) => (GraphQLType) null); // TODO: mutations + mutationType
            schemaType.AddField("directives", (db, s) => new List<GraphQLType>()); // TODO: Directives

            var typeType = AddType<GraphQLType>("__Type");
            typeType.AddField("kind", (db, t) => GetTypeKind(t));
            typeType.AddField(t => t.Name);
            typeType.AddField(t => t.Description);
            typeType.AddField(t => t.Fields); // TODO: includeDeprecated
            typeType.AddField("interfaces", (db, t) => new List<GraphQLType>());

            var fieldType = AddType<GraphQLField>("__Field");
            fieldType.AddField(f => f.Name);
            fieldType.AddField(f => f.Description);
            //field.AddField(f => f.Arguments); // TODO:
            fieldType.AddField(f => f.Type);
            fieldType.AddField("isDeprecated", (db, f) => false); // TODO: deprecation
            fieldType.AddField("deprecationReason", (db, f) => "");

            this.AddQuery("__schema", db => this);
            this.AddQuery("__type", new {name = ""}, (db, args) => _types.AsQueryable().Where(t => t.Name == args.name).First());

            var method = GetType().GetMethod("AddTypeNameField", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var type in _types.Where(t => !t.IsScalar))
            {
                var genMethod = method.MakeGenericMethod(type.CLRType);
                genMethod.Invoke(this, new object[] {type});
            }
        }

        private static string GetTypeKind(GraphQLType type)
        {
            if (type.IsScalar)
                return "SCALAR";
            return "OBJECT";
            // TODO: interface?, union? enum, input_object, list, non_null
        }

        private void AddTypeNameField<TEntity>(GraphQLType type)
        {
            var builder = new GraphQLTypeBuilder<TContext, TEntity>(this, type);
            builder.AddPostField("__typename", () => type.Name);
        }

        // This signature is pretty complicated, but necessarily so.
        // We need to build a function that we can execute against passed in TArgs that
        // will return a base expression for combining with selectors (stored on GraphQLType.Fields)
        // This used to be a Func<TContext, TArgs, IQueryable<TEntity>>, i.e. a function that returned a queryable given a context and arguments.
        // However, this wasn't good enough since we needed to be able to reference the (db) parameter in the expressions.
        // For example, being able to do:
        //     db.Users.Select(u => new {
        //         TotalFriends = db.Friends.Count(f => f.UserId = u.Id)
        //     })
        // This meant that the (db) parameter in db.Friends had to be the same (db) parameter in db.Users
        // The only way to do this is to generate the entire expression, i.e. starting with db.Users...
        // The type of that expression is the same as the type of our original Func, but wrapped in Expression<>, so:
        //    Expression<TQueryFunc> where TQueryFunc = Func<TContext, IQueryable<TEntity>>
        // Since the query will change based on arguments, we need a function to generate the above Expression
        // based on whatever arguments are passed in, so:
        //    Func<TArgs, Expression<TQueryFunc>> where TQueryFunc = Func<TContext, IQueryable<TEntity>>
        internal GraphQLQueryBuilder<TArgs> AddQueryInternal<TArgs, TEntity>(string name, Func<TArgs, Expression<Func<TContext, IQueryable<TEntity>>>> exprGetter, ResolutionType type)
        {
            if (FindQuery(name) != null)
                throw new Exception($"Query named {name} has already been created.");
            var query = new GraphQLQuery<TContext, TArgs, TEntity>
            {
                Name = name,
                Type = GetGQLType(typeof (TEntity)),
                QueryableExprGetter = exprGetter,
                Schema = this,
                ResolutionType = type,
            };
            _queries.Add(query);
            return GraphQLQueryBuilder<TArgs>.New(query);
        }

        internal GraphQLQueryBuilder<TArgs> AddUnmodifiedQueryInternal<TArgs, TEntity>(string name, Func<TArgs, Expression<Func<TContext, TEntity>>> exprGetter)
        {
            if (FindQuery(name) != null)
                throw new Exception($"Query named {name} has already been created.");
            var query = new GraphQLQuery<TContext, TArgs, TEntity>
            {
                Name = name,
                Type = GetGQLType(typeof(TEntity)),
                ExprGetter = exprGetter,
                Schema = this,
                ResolutionType = ResolutionType.Unmodified,
            };
            _queries.Add(query);
            return GraphQLQueryBuilder<TArgs>.New(query);
        }

        internal GraphQLQueryBase<TContext> FindQuery(string name) => _queries.FirstOrDefault(q => q.Name == name);

        internal override GraphQLType GetGQLType(Type type)
            => _types.FirstOrDefault(t => t.CLRType == type)
                ?? VariableTypes.IntrospectionTypes.FirstOrDefault(f => f.CLRType == type)
                ?? new GraphQLType(type) { IsScalar = true };

        internal IEnumerable<GraphQLQueryBase<TContext>> Queries => _queries;
        internal IEnumerable<GraphQLType> Types => _types;
    }
}
