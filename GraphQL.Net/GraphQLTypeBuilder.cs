using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace GraphQL.Net
{
    public class GraphQLTypeBuilder<TContext, TEntity>
    {
        private readonly GraphQLSchema<TContext> _schema;
        private readonly GraphQLType _type;

        internal GraphQLTypeBuilder(GraphQLSchema<TContext> schema, GraphQLType type)
        {
            _schema = schema;
            _type = type;
        }

        // This overload is provided to the user so they can shape TArgs with an anonymous type and rely on type inference for type parameters
        // e.g.  AddField("profilePic", new { size = 0 }, (db, user) => db.ProfilePics.Where(p => p.UserId == u.Id && p.Size == args.size));
        public GraphQLFieldBuilder<TArgs> AddField<TArgs, TField>(string name, TArgs shape, Func<TArgs, Expression<Func<TContext, TEntity, TField>>> exprFunc)
            => AddField(name, exprFunc);

        // See GraphQLSchema.AddQuery for an explanation of the type of exprFunc, since it follows similar reasons
        // TL:DR; Fields can have parameters passed in, so the Expression<Func> to be used is dependent on TArgs
        //        Fields can use TContext as well, so we have to return an Expression<Func<TContext, TEntity, TField>> and replace the TContext parameter when needed
        public GraphQLFieldBuilder<TArgs> AddField<TArgs, TField>(string name, Func<TArgs, Expression<Func<TContext, TEntity, TField>>> exprFunc)
        {
            var field = GraphQLField.New(_schema, name, exprFunc, typeof (TField));
            _type.Fields.Add(field);
            return new GraphQLFieldBuilder<TArgs>(field);
        }

        // Overload provided for easily adding properties, e.g.  AddField(u => u.Name);
        public GraphQLFieldBuilder<object> AddField<TField>(Expression<Func<TEntity, TField>> expr)
        {
            var member = expr.Body as MemberExpression;
            if (member == null)
                throw new InvalidOperationException($"Unnamed query {nameof(expr)} must be a MemberExpression of form [p => p.Field].\n\nTry using the explicit AddField overload for a custom field.");
            var name = member.Member.Name;
            var lambda = Expression.Lambda<Func<TContext, TEntity, TField>>(member, GraphQLSchema<TContext>.DbParam, expr.Parameters[0]);
            return AddField(name.ToCamelCase(), lambda);
        }

        // Overload provided for adding fields with no arguments, e.g.  AddField("totalCount", (db, u) => db.Users.Count());
        public GraphQLFieldBuilder<object> AddField<TField>(string name, Expression<Func<TContext, TEntity, TField>> expr)
            => AddField(name, new object(), o => expr);

        public void AddAllFields()
        {
            foreach (var prop in typeof (TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                _type.Fields.Add(CreateGenericField(prop));
        }

        // unsafe generic magic to create a GQLField instance
        private GraphQLField CreateGenericField(PropertyInfo prop)
        {
            // build selector expression, e.g.: (db, p) => p.Id
            var entityParam = Expression.Parameter(typeof(TEntity), "p");
            var memberExpr = Expression.MakeMemberAccess(entityParam, prop);
            var lambda = Expression.Lambda(memberExpr, GraphQLSchema<TContext>.DbParam, entityParam);

            // build args func wrapping selector expression, e.g. o => (db, p) => p.Id
            var objectParam = Expression.Parameter(typeof(object), "o");
            var argsExpr = Expression.Lambda(Expression.Quote(lambda), objectParam);
            var exprFunc = argsExpr.Compile();

            return GraphQLField.New(_schema, prop.Name.ToCamelCase(), (Func<object, LambdaExpression>) exprFunc, prop.PropertyType);
        }

        public GraphQLFieldBuilder<object> AddPostField<TField>(string name, Func<TField> fieldFunc)
        {
            var field = GraphQLField.Post(_schema, name, fieldFunc);
            _type.Fields.Add(field);
            return new GraphQLFieldBuilder<object>(field);
        }
    }
}